using System.Text.Json;
using Npgsql;
using Zentriq.Domain.Licensing;

namespace Zentriq.LicenseServer.Services;

public sealed record ServiceResult<T>(bool Ok, int Status, string Message, T? Value = default)
{
    public static ServiceResult<T> Fail(int status, string message) => new(false, status, message);
}

public sealed class Audit(NpgsqlDataSource ds)
{
    public async Task WriteAsync(string actor, string action, string? target, string? ip, object? detail, CancellationToken ct, NpgsqlConnection? conn = null, NpgsqlTransaction? tx = null)
    {
        var json = detail is null ? null : JsonSerializer.Serialize(detail, JsonDefaults.Web);
        const string sql = "INSERT INTO zls.audit_log (actor, action, target, ip, detail) VALUES (@actor, @action, @target, @ip, @detail::jsonb)";
        var args = new { actor, action, target = (object?)target, ip = (object?)ip, detail = (object?)json };
        if (conn is not null) { await conn.ExecAsync(sql, args, ct, tx); return; }
        await using var c = await ds.OpenConnectionAsync(ct);
        await c.ExecAsync(sql, args, ct);
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// The public licensing protocol used by Zentriq installations: online activation with a license key, check-ins
/// (refreshed license with a new lease, renewals, upgrades, suspension, revocation), deactivation, and offline
/// activation for air-gapped servers (license key + activation request file → license file).
/// </summary>
public sealed class LicenseService(NpgsqlDataSource ds, LicenseIssuer issuer, Audit audit)
{
    private const int MaxUsageKeys = 32;

    public async Task<(License L, Customer C)?> FindByKeyAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string normalizedKey, CancellationToken ct)
    {
        var l = await conn.FirstAsync($"SELECT {License.Columns} FROM zls.licenses WHERE key_hash = @h FOR UPDATE",
            new { h = LicenseKeys.Hash(normalizedKey) }, License.Read, ct, tx);
        if (l is null) return null;
        var c = await conn.FirstAsync($"SELECT {Customer.Columns} FROM zls.customers WHERE id = @id", new { id = l.CustomerId }, Customer.Read, ct, tx);
        return (l, c!);
    }

    public async Task<(License L, Customer C)?> FindByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string id, CancellationToken ct)
    {
        var l = await conn.FirstAsync($"SELECT {License.Columns} FROM zls.licenses WHERE id = @id FOR UPDATE", new { id }, License.Read, ct, tx);
        if (l is null) return null;
        var c = await conn.FirstAsync($"SELECT {Customer.Columns} FROM zls.customers WHERE id = @id", new { id = l.CustomerId }, Customer.Read, ct, tx);
        return (l, c!);
    }

    private static string? Refusal(License l, DateTimeOffset now) => l.Status switch
    {
        "revoked" => "This license key has been revoked. Contact Zentriq.",
        "suspended" => "This license is suspended. Contact Zentriq.",
        _ when l.Expired(now) => $"This subscription ended on {l.ExpiresAt:yyyy-MM-dd}. Renew it to activate.",
        _ => null,
    };

    private static string UsageJson(IReadOnlyDictionary<string, int>? usage) =>
        JsonSerializer.Serialize((usage ?? new Dictionary<string, int>()).Take(MaxUsageKeys)
            .Where(kv => kv.Key.Length <= 40).ToDictionary(kv => kv.Key, kv => Math.Max(0, kv.Value)));

    private static string? Validate(ActivationRequest? r)
    {
        if (r is null) return "The activation request is missing.";
        if (r.Format != LicenseFormats.ActivationRequest) return "This is not a Zentriq activation request.";
        if (r.InstallationId == Guid.Empty) return "The activation request has no installation id.";
        if ((r.MachineName?.Length ?? 0) > 255 || (r.Organization?.Length ?? 0) > 255 || (r.ProductVersion?.Length ?? 0) > 64) return "The activation request is not valid.";
        return null;
    }

    /// <summary>Binds a license key to an installation (online or offline) and returns the signed license.</summary>
    public async Task<ServiceResult<(string Document, string? Token, LicensePayload Payload)>> ActivateAsync(
        string? licenseKey, ActivationRequest? request, string mode, string? ip, CancellationToken ct, string? licenseFile = null)
    {
        var byFile = !string.IsNullOrWhiteSpace(licenseFile);
        var key = byFile ? null : LicenseKeys.Normalize(licenseKey);
        if (!byFile && key is null) return new(false, 400, "Use the license file from Zentriq, or a license key of the form ZQL-XXXXX-XXXXX-XXXXX-XXXXX.");
        if (Validate(request) is { } bad) return new(false, 400, bad);
        var now = issuer.Now;

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        (License L, Customer C)? found;
        if (byFile)
        {
            // The license file proves the purchase: genuine (our signature), for a known license, and the newest file issued.
            var payload = licenseFile!.Length <= LicenseFormats.MaxDocumentBytes ? issuer.ReadLicenseFile(licenseFile) : null;
            if (payload is null)
            {
                await audit.WriteAsync("public", "activate.bad_file", null, ip, new { mode }, ct, conn, tx);
                await tx.CommitAsync(ct);
                return new(false, 422, "This is not a valid Zentriq license file. Use the file Zentriq sent you, unchanged.");
            }
            found = await FindByIdAsync(conn, tx, payload.LicenseId, ct);
            if (found is { } ff && ff.L.FileHash != LicenseIssuer.FileHash(licenseFile))
            {
                await audit.WriteAsync("public", "activate.replaced_file", ff.L.Id, ip, new { mode }, ct, conn, tx);
                await tx.CommitAsync(ct);
                return new(false, 403, "This license file has been replaced by a newer one. Use the latest license file from Zentriq.");
            }
        }
        else found = await FindByKeyAsync(conn, tx, key!, ct);
        if (found is not { } f)
        {
            await audit.WriteAsync("public", byFile ? "activate.unknown_file" : "activate.unknown_key", byFile ? null : LicenseKeys.Hint(key!), ip, new { mode }, ct, conn, tx);
            await tx.CommitAsync(ct);
            return new(false, 404, byFile ? "This license is not known to the license server. Contact Zentriq." : "This license key is not known. Check the key, or contact Zentriq.");
        }
        var (l, c) = f;
        if (Refusal(l, now) is { } refusal)
        {
            await audit.WriteAsync("public", "activate.refused", l.Id, ip, new { mode, refusal }, ct, conn, tx);
            await tx.CommitAsync(ct);
            return new(false, 403, refusal);
        }

        var existing = await conn.FirstAsync($"SELECT {Activation.Columns} FROM zls.activations WHERE license_id = @l AND installation_id = @i AND status = 'active'",
            new { l = l.Id, i = request!.InstallationId }, Activation.Read, ct, tx);
        if (existing is null)
        {
            var active = Convert.ToInt32(await conn.ScalarAsync("SELECT count(*) FROM zls.activations WHERE license_id = @l AND status = 'active'", new { l = l.Id }, ct, tx));
            if (active >= l.MaxActivations)
            {
                await audit.WriteAsync("public", "activate.limit", l.Id, ip, new { mode, active, l.MaxActivations, request.InstallationId }, ct, conn, tx);
                await tx.CommitAsync(ct);
                return new(false, 409, l.MaxActivations == 1
                    ? "This license key is already activated on another Zentriq server. Deactivate it there first (Licensing › Deactivate), or contact Zentriq to move it."
                    : $"This license key is already activated on {active} Zentriq servers, the most it allows. Deactivate one of them first.");
            }
        }

        string? token = mode == LicenseFormats.Online ? Tokens.New() : null;
        var activationId = existing?.Id ?? Guid.NewGuid();
        var args = new
        {
            id = activationId, lic = l.Id, inst = request.InstallationId, mode, token = (object?)(token is null ? null : Tokens.Hash(token)),
            req = (object?)request.RequestId, machine = (object?)request.MachineName, org = (object?)request.Organization,
            ver = (object?)request.ProductVersion, usage = UsageJson(request.CurrentUsage), now,
        };
        if (existing is null)
            await conn.ExecAsync("""
                INSERT INTO zls.activations (id, license_id, installation_id, mode, token_hash, request_id, machine_name, organization, product_version, usage, activated_at, last_checkin_at)
                VALUES (@id, @lic, @inst, @mode, @token, @req, @machine, @org, @ver, @usage::jsonb, @now, @now)
                """, args, ct, tx);
        else
            // Activating again on the same installation (reinstall, lost token, switch between online and offline): one activation, new token.
            await conn.ExecAsync("""
                UPDATE zls.activations SET mode = @mode, token_hash = @token, request_id = @req, machine_name = @machine, organization = @org,
                       product_version = @ver, usage = @usage::jsonb, last_checkin_at = @now WHERE id = @id
                """, args, ct, tx);

        var (doc, payload) = await issuer.IssueAsync(conn, tx, l, c, request.InstallationId, request.RequestId, activationId, mode, existing is null ? "activate" : "reactivate", ct);
        await audit.WriteAsync("public", mode == LicenseFormats.Online ? "activate.online" : "activate.offline", l.Id, ip,
            new { request.InstallationId, request.MachineName, request.Organization, request.ProductVersion, again = existing is not null, with = byFile ? "license file" : "license key" }, ct, conn, tx);
        await tx.CommitAsync(ct);
        var msg = l.IsPerpetual
            ? $"Perpetual license, support until {l.SupportUntil:yyyy-MM-dd}."
            : $"Subscription until {l.ExpiresAt:yyyy-MM-dd}.";
        return new(true, 200, msg, (doc, token, payload));
    }

    private async Task<(Activation A, License L, Customer C)?> FindByTokenAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string? token, Guid installationId, string licenseId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;
        var a = await conn.FirstAsync($"SELECT {Activation.Columns} FROM zls.activations WHERE token_hash = @h ORDER BY activated_at DESC LIMIT 1 FOR UPDATE",
            new { h = Tokens.Hash(token) }, Activation.Read, ct, tx);
        if (a is null || a.InstallationId != installationId || a.LicenseId != licenseId) return null;
        var l = await conn.FirstAsync($"SELECT {License.Columns} FROM zls.licenses WHERE id = @id", new { id = a.LicenseId }, License.Read, ct, tx);
        var c = await conn.FirstAsync($"SELECT {Customer.Columns} FROM zls.customers WHERE id = @id", new { id = l!.CustomerId }, Customer.Read, ct, tx);
        return (a, l, c!);
    }

    /// <summary>Check-in: records usage and answers with a refreshed license, or with the end of the activation.</summary>
    public async Task<ServiceResult<OnlineCheckinResponse>> CheckinAsync(string? token, OnlineCheckinRequest? body, string? ip, CancellationToken ct)
    {
        if (body is null) return ServiceResult<OnlineCheckinResponse>.Fail(400, "The check-in is empty.");
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var found = await FindByTokenAsync(conn, tx, token, body.InstallationId, body.LicenseId, ct);
        if (found is not { } f) return ServiceResult<OnlineCheckinResponse>.Fail(401, "This activation is not known to the license server. Activate the license again.");
        var (a, l, c) = f;
        var now = issuer.Now;
        await conn.ExecAsync("""
            UPDATE zls.activations SET last_checkin_at = @now, usage = @usage::jsonb, product_version = @ver, release_date = @rel WHERE id = @id
            """, new { now, usage = UsageJson(body.Usage), ver = (object?)body.ProductVersion?[..Math.Min(64, body.ProductVersion.Length)], rel = body.ReleaseDate, id = a.Id }, ct, tx);

        OnlineCheckinResponse answer;
        if (a.Status == "deactivated")
            answer = new("deactivated", null, "This server's activation was released in the license portal. Activate it again with a license key.");
        else if (l.Status == "revoked")
            answer = new("revoked", null, "This license has been revoked. Contact Zentriq.");
        else if (l.Status == "suspended")
            answer = new("suspended", null, "This license is suspended. Contact Zentriq.");
        else
        {
            var (doc, _) = await issuer.IssueAsync(conn, tx, l, c, a.InstallationId, a.RequestId, a.Id, LicenseFormats.Online, "checkin", ct);
            var note = l.IsPerpetual
                ? (body.ReleaseDate > l.SupportUntil ? $"Support ended on {l.SupportUntil:yyyy-MM-dd}, before this version was released. Renew support to run it." : $"Support until {l.SupportUntil:yyyy-MM-dd}.")
                : (l.Expired(now) ? $"The subscription ended on {l.ExpiresAt:yyyy-MM-dd}. Renew it." : $"Subscription until {l.ExpiresAt:yyyy-MM-dd}.");
            answer = new("active", doc, note);
        }
        await audit.WriteAsync("public", "checkin." + answer.Status, l.Id, ip, new { a.InstallationId, body.ProductVersion }, ct, conn, tx);
        await tx.CommitAsync(ct);
        return new(true, 200, answer.Message, answer);
    }

    public async Task<ServiceResult<string>> DeactivateAsync(string? token, OnlineDeactivateRequest? body, string? ip, CancellationToken ct)
    {
        if (body is null) return ServiceResult<string>.Fail(400, "The request is empty.");
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var found = await FindByTokenAsync(conn, tx, token, body.InstallationId, body.LicenseId, ct);
        if (found is not { } f) return ServiceResult<string>.Fail(401, "This activation is not known to the license server.");
        await conn.ExecAsync("UPDATE zls.activations SET status = 'deactivated', token_hash = NULL, deactivated_at = @now, deactivated_by = 'installation' WHERE id = @id",
            new { now = issuer.Now, id = f.A.Id }, ct, tx);
        await audit.WriteAsync("public", "deactivate", f.L.Id, ip, new { f.A.InstallationId }, ct, conn, tx);
        await tx.CommitAsync(ct);
        return new(true, 200, "Released.", "Released");
    }
}
