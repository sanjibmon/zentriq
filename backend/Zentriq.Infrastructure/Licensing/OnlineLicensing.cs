using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zentriq.Application.Common;
using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;
using Zentriq.Infrastructure.Audit;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Licensing;

public sealed record ImportResult(bool Ok, int Status, string Message, LicensePayload? Payload = null, string? Reason = null);

/// <summary>Stores a signed license after verifying it: a new license replaces the active one; a refresh of the same license updates it.</summary>
public sealed class LicenseImporter(ZentriqDbContext db, LicenseVerifier verifier, IClock clock)
{
    public async Task<ImportResult> ImportAsync(string document, Guid? userId, CancellationToken ct)
    {
        var state = await db.InstallState.AsNoTracking().FirstOrDefaultAsync(ct);
        if (state?.InitializedAt is null) return new(false, 409, "Complete setup before importing a license.");
        var v = verifier.Verify(document ?? "", state.InstallationId, clock.UtcNow);
        if (!v.IsValid) return new(false, 422, v.Message, Reason: v.Rejection.ToString());
        var p = v.Payload!;
        var keyId = JsonDocument.Parse(document!).RootElement.GetProperty("keyId").GetString()!;

        var existing = await db.LicenseRecords.Include(l => l.Entitlements).FirstOrDefaultAsync(l => l.LicenseId == p.LicenseId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (existing is not null)
        {
            // The same license again: only a newer issue (renewal, upgrade, new lease from the license server) replaces it.
            if (p.IssuedAt <= existing.IssuedAt) return new(false, 409, "This license has already been imported.");
            if (existing.Status != "active")
                await db.LicenseRecords.Where(l => l.Status == "active").ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "superseded"), ct);
            Fill(existing, p, keyId, document!, userId);
            existing.Entitlements.Clear();
            existing.Entitlements.AddRange(p.Entitlements.Select(e => new LicenseEntitlement { Workload = e.Key, Quantity = e.Value }));
        }
        else
        {
            await db.LicenseRecords.Where(l => l.Status == "active").ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "superseded"), ct);
            var rec = new LicenseRecord { Id = Guid.NewGuid() };
            Fill(rec, p, keyId, document!, userId);
            rec.Entitlements = p.Entitlements.Select(e => new LicenseEntitlement { Workload = e.Key, Quantity = e.Value }).ToList();
            db.LicenseRecords.Add(rec);
        }
        if (p.RequestId is { } reqId)
            await db.LicenseActivations.Where(a => a.RequestId == reqId).ExecuteUpdateAsync(s => s.SetProperty(a => a.FulfilledByLicense, p.LicenseId), ct);
        if (p.Licensee is { } licensee && await db.Organizations.OrderBy(o => o.CreatedAt).FirstOrDefaultAsync(ct) is { } org)
            ApplyLicensee(org, licensee);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(true, 200, existing is null ? "License imported." : "License updated.", p);
    }

    private void Fill(LicenseRecord r, LicensePayload p, string keyId, string document, Guid? userId)
    {
        r.LicenseId = p.LicenseId; r.SchemaVersion = p.SchemaVersion; r.KeyId = keyId; r.CustomerName = p.Customer; r.InstallationId = p.InstallationId;
        r.IssuedAt = p.IssuedAt.ToUniversalTime(); r.NotBefore = p.NotBefore.ToUniversalTime(); r.ExpiresAt = p.ExpiresAt.ToUniversalTime();
        r.GraceDays = p.GraceDays; r.Modules = p.Modules.ToArray(); r.RawDocument = document; r.Status = "active";
        r.ImportedBy = userId; r.ImportedAt = clock.UtcNow;
    }

    /// <summary>Fills empty profile fields from the signed licensee block (never overwrites administrator edits).</summary>
    public static void ApplyLicensee(Organization org, Licensee l)
    {
        org.LegalName ??= l.Company;
        org.ContactName ??= l.ContactName;
        org.ContactEmail ??= l.ContactEmail;
        org.ContactPhone ??= l.ContactPhone;
        org.AddressLine1 ??= l.Address;
        org.Country ??= l.Country;
        org.TaxId ??= l.TaxId;
        org.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>The HTTP client used to reach the license server (replaceable in tests).</summary>
public sealed class LicenseServerHttp(HttpClient client)
{
    public HttpClient Client { get; } = client;
    public static LicenseServerHttp CreateDefault() => new(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "Zentriq/" + (typeof(LicenseServerHttp).Assembly.GetName().Version?.ToString(3) ?? "1") } },
    });
}

/// <summary>Online activation state (system_settings 'licensing.online'); the activation token is encrypted.</summary>
public sealed class OnlineState
{
    public string? ServerUrl { get; set; }
    public string? LicenseId { get; set; }
    public string? KeyHint { get; set; }
    public string? Token { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public string? ActivatedBy { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastCheckinAt { get; set; }
    public string? LastStatus { get; set; }
    public string? LastMessage { get; set; }
    public bool Active { get; set; }
}

public sealed record OnlineResult(bool Ok, string Message, int Status = 200);

/// <summary>
/// Online activation with a license key, daily check-ins (usage report, refreshed license, renewals, revocation) and
/// deactivation (to move the license to another server). Air-gapped servers use the activation request / license file.
/// </summary>
public sealed class OnlineLicensing(
    ZentriqDbContext db, NpgsqlDataSource ds, LicensingService licensing, LicenseImporter importer, CredentialProtector protector,
    AuditWriter audit, LicenseServerHttp http, ZentriqOptions options, IClock clock)
{
    public const string StateKey = "licensing.online";
    public const string ServerKey = "licensing.server_url";
    private static readonly Guid TokenPurpose = Guid.Parse("5c0f2a1e-7d4b-4e3a-9a61-1f2b3c4d5e6f");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<string> ServerUrlAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value #>> '{}' FROM zentriq.system_settings WHERE key = @k");
        cmd.Parameters.AddWithValue("k", ServerKey);
        var v = await cmd.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(v) ? LicenseServerProtocol.DefaultServerUrl : v.Trim().TrimEnd('/');
    }

    public async Task SetServerUrlAsync(string url, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value) VALUES (@k, to_jsonb(@v::text))
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("k", ServerKey);
        cmd.Parameters.AddWithValue("v", url.Trim().TrimEnd('/'));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<OnlineState> LoadStateAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = @k");
        cmd.Parameters.AddWithValue("k", StateKey);
        return await cmd.ExecuteScalarAsync(ct) is string s ? JsonSerializer.Deserialize<OnlineState>(s, Json) ?? new() : new();
    }

    private async Task SaveStateAsync(OnlineState st, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value) VALUES (@k, @v::jsonb)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("k", StateKey);
        cmd.Parameters.AddWithValue("v", JsonSerializer.Serialize(st, Json));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<ActivationRequest> RequestAsync(CancellationToken ct)
    {
        var state = await db.InstallState.AsNoTracking().FirstAsync(ct);
        var org = await db.Organizations.AsNoTracking().OrderBy(o => o.CreatedAt).Select(o => o.Name).FirstOrDefaultAsync(ct) ?? "";
        var usage = await licensing.CountUsageAsync(null, null, ct);
        return new ActivationRequest(LicenseFormats.ActivationRequest, LicenseFormats.CurrentSchemaVersion, Guid.NewGuid(), state.InstallationId,
            options.ProductVersion, Environment.MachineName, org, clock.UtcNow, usage);
    }

    private static Uri Endpoint(string server, string path) => new(new Uri(server.TrimEnd('/') + "/"), path);

    private static async Task<string> ProblemAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("title", out var t)) return t.GetString() ?? resp.ReasonPhrase ?? "";
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("message", out var m)) return m.GetString() ?? resp.ReasonPhrase ?? "";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        return $"The license server answered {(int)resp.StatusCode} {resp.ReasonPhrase}.";
    }

    /// <summary>Activates this installation with a license key (the license server binds the key to this installation).</summary>
    public async Task<OnlineResult> ActivateAsync(string licenseKey, Guid? userId, string? userName, CancellationToken ct)
    {
        var key = LicenseKeys.Normalize(licenseKey);
        if (key is null) return new(false, "A license key has the form ZQL-XXXXX-XXXXX-XXXXX-XXXXX.", 400);
        var server = await ServerUrlAsync(ct);
        var request = await RequestAsync(ct);
        OnlineActivateResponse? answer;
        try
        {
            using var resp = await http.Client.PostAsJsonAsync(Endpoint(server, LicenseServerProtocol.ActivatePath), new OnlineActivateRequest(key, request), Json, ct);
            if (!resp.IsSuccessStatusCode) return new(false, await ProblemAsync(resp, ct), (int)resp.StatusCode is >= 400 and < 500 ? 422 : 502);
            answer = await resp.Content.ReadFromJsonAsync<OnlineActivateResponse>(Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(false, $"The license server at {server} could not be reached ({ex.Message}). Check this server's internet access and proxy, or use offline activation.", 502);
        }
        if (answer is null || string.IsNullOrEmpty(answer.License)) return new(false, "The license server sent no license.", 502);
        var imported = await importer.ImportAsync(answer.License, userId, ct);
        if (!imported.Ok) return new(false, "The license from the license server was not accepted: " + imported.Message, 502);
        await SaveStateAsync(new OnlineState
        {
            ServerUrl = server, LicenseId = imported.Payload!.LicenseId, KeyHint = LicenseKeys.Hint(key),
            Token = answer.ActivationToken is { Length: > 0 } t ? protector.Protect(TokenPurpose, new CredentialSecret { Password = t }) : null,
            ActivatedAt = clock.UtcNow, ActivatedBy = userName, LastAttemptAt = clock.UtcNow, LastCheckinAt = clock.UtcNow, LastStatus = "active",
            LastMessage = answer.Message, Active = true,
        }, ct);
        await audit.WriteAsync(new AuditActor(userId, userName ?? "Zentriq", null), "license.activate_online", imported.Payload.LicenseId,
            detail: new { server, key = LicenseKeys.Hint(key), imported.Payload.Customer, imported.Payload.Edition, imported.Payload.Entitlements }, ct: ct);
        return new(true, $"Activated: {imported.Payload.Customer}, {Editions.Title(imported.Payload.Edition)} edition. {answer.Message}".Trim());
    }

    /// <summary>
    /// Check-in: reports usage and takes the license server's answer — a refreshed license (renewal, upgrade, support,
    /// new lease), or the end of the activation (suspended, revoked, deactivated in the license portal).
    /// </summary>
    public async Task<OnlineResult> CheckinAsync(string trigger, CancellationToken ct)
    {
        var st = await LoadStateAsync(ct);
        if (!st.Active || st.Token is null || st.LicenseId is null) return new(false, "This server is not activated online.", 409);
        var server = st.ServerUrl ?? await ServerUrlAsync(ct);
        var installation = (await db.InstallState.AsNoTracking().FirstAsync(ct)).InstallationId;
        st.LastAttemptAt = clock.UtcNow;
        OnlineCheckinResponse? answer;
        try
        {
            var token = protector.Unprotect(TokenPurpose, st.Token).Password;
            using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint(server, LicenseServerProtocol.CheckinPath))
            {
                Content = JsonContent.Create(new OnlineCheckinRequest(installation, st.LicenseId, options.ProductVersion, ProductInfo.ReleaseDate,
                    await licensing.CountUsageAsync(null, null, ct)), options: Json),
            };
            msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.Client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                st.LastStatus = "error"; st.LastMessage = await ProblemAsync(resp, ct);
                await SaveStateAsync(st, ct);
                return new(false, st.LastMessage, 502);
            }
            answer = await resp.Content.ReadFromJsonAsync<OnlineCheckinResponse>(Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            st.LastStatus = "error"; st.LastMessage = $"The license server at {server} could not be reached: {ex.Message}";
            await SaveStateAsync(st, ct);
            return new(false, st.LastMessage, 502);
        }
        if (answer is null) return new(false, "The license server sent an empty answer.", 502);

        st.LastStatus = answer.Status; st.LastMessage = answer.Message; st.LastCheckinAt = clock.UtcNow;
        if (answer.Status == "active" && answer.License is { Length: > 0 } doc)
        {
            var r = await importer.ImportAsync(doc, null, ct);
            if (!r.Ok && r.Status != 409) { st.LastStatus = "error"; st.LastMessage = "The refreshed license was not accepted: " + r.Message; }
            else if (r.Ok) await audit.WriteAsync(new AuditActor(null, "Zentriq license check-in", null), "license.refresh", r.Payload!.LicenseId,
                detail: new { trigger, r.Payload.ExpiresAt, r.Payload.SupportUntil, r.Payload.LeaseUntil, r.Payload.Entitlements }, ct: ct);
        }
        else if (answer.Status is "revoked" or "suspended" or "deactivated")
        {
            // The license no longer applies on this server: the installation falls back to the free tier (data is kept).
            await db.LicenseRecords.Where(l => l.LicenseId == st.LicenseId && l.Status == "active").ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "revoked"), ct);
            st.Active = answer.Status == "suspended";   // a suspended license may be resumed: keep checking in
            await audit.WriteAsync(new AuditActor(null, "Zentriq license check-in", null), "license." + answer.Status, st.LicenseId, "Denied", new { answer.Message }, ct: ct);
        }
        await SaveStateAsync(st, ct);
        return new(st.LastStatus != "error", answer.Message);
    }

    /// <summary>Releases the activation on the license server (to use the key on another server) and removes the license here.</summary>
    public async Task<OnlineResult> DeactivateAsync(Guid? userId, string? userName, CancellationToken ct)
    {
        var st = await LoadStateAsync(ct);
        if (!st.Active || st.Token is null || st.LicenseId is null) return new(false, "This server is not activated online.", 409);
        var server = st.ServerUrl ?? await ServerUrlAsync(ct);
        var installation = (await db.InstallState.AsNoTracking().FirstAsync(ct)).InstallationId;
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, Endpoint(server, LicenseServerProtocol.DeactivatePath))
            {
                Content = JsonContent.Create(new OnlineDeactivateRequest(installation, st.LicenseId), options: Json),
            };
            msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", protector.Unprotect(TokenPurpose, st.Token).Password);
            using var resp = await http.Client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode) return new(false, await ProblemAsync(resp, ct), 502);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Security.Cryptography.CryptographicException)
        {
            return new(false, $"The license server at {server} could not be reached: {ex.Message}. Deactivation needs the license server.", 502);
        }
        await db.LicenseRecords.Where(l => l.LicenseId == st.LicenseId && l.Status == "active").ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, "superseded"), ct);
        var id = st.LicenseId;
        await SaveStateAsync(new OnlineState { ServerUrl = st.ServerUrl, LastStatus = "deactivated", LastMessage = "Deactivated on " + clock.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC." }, ct);
        await audit.WriteAsync(new AuditActor(userId, userName ?? "Zentriq", null), "license.deactivate_online", id, ct: ct);
        return new(true, "The license was released on the license server; it can now be activated on another server.");
    }

    /// <summary>Whether a check-in is due: once a day, or every hour while the lease ends within 10 days.</summary>
    public static bool CheckinDue(OnlineState st, DateTimeOffset? leaseUntil, DateTimeOffset now) =>
        st.Active && st.Token is not null &&
        (st.LastAttemptAt is not { } a || now - a > TimeSpan.FromHours(20) ||
         (leaseUntil is { } l && l - now < TimeSpan.FromDays(10) && now - a > TimeSpan.FromHours(1)));
}
