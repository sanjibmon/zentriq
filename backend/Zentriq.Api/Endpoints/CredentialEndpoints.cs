using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Credential vault for agentless scanning. Secrets are write-only: they are encrypted on receipt
/// (AES-256-GCM, installation credential key) and no endpoint ever returns them.
/// </summary>
public static class CredentialEndpoints
{
    public sealed record SecretBody(string? Password, string? PrivateKey, string? Passphrase, string? Community, string? AuthPassword, string? PrivPassword);
    public sealed record CredentialBody(string Name, string Kind, string? Username, string? Description, string? AllowedScope, JsonElement? Options, SecretBody? Secret);

    private static readonly string[] Kinds = ["windows_domain", "windows_local", "ssh_password", "ssh_key", "snmp_v2c", "snmp_v3", "ldap_bind"];

    public static void MapCredentialEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/credentials").WithTags("Credentials");

        // Names only (no secrets) so scan operators can pick credentials.
        g.MapGet("", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT id, name, kind, username, description, allowed_scope, options::text, created_at, rotated_at, last_used_at
                  FROM zentriq.credentials ORDER BY name
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), kind = r.GetString(2), username = r.IsDBNull(3) ? null : r.GetString(3),
                    description = r.IsDBNull(4) ? null : r.GetString(4), allowedScope = r.IsDBNull(5) ? null : r.GetString(5),
                    options = JsonDocument.Parse(r.GetString(6)).RootElement, createdAt = r.GetFieldValue<DateTimeOffset>(7),
                    rotatedAt = r.GetFieldValue<DateTimeOffset>(8), lastUsedAt = r.IsDBNull(9) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(9),
                });
            return Results.Ok(list);
        }).RequireAuthorization(Permissions.ScansRun);

        g.MapPost("", async (CredentialBody body, NpgsqlDataSource ds, CredentialProtector protector, RequestContext rc, CancellationToken ct) =>
        {
            var err = Validate(body, requireSecret: true);
            if (err is not null) return Validation.Problem(err.Value.Field, err.Value.Message);
            var id = Guid.NewGuid();
            var secret = ToSecret(body.Secret!);
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.credentials (id, name, kind, username, secret_protected, allowed_scope, options, description, created_by)
                VALUES (@id, @n, @k, @u, @s, @scope, @o, @d, @by)
                """);
            cmd.Parameters.AddWithValue("id", id);
            Bind(cmd, body, rc.UserId);
            cmd.Parameters.AddWithValue("s", protector.Protect(id, secret));
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A credential with this name already exists."); }
            await rc.AuditAsync("credential.create", body.Name, detail: new { body.Kind, body.Username, body.AllowedScope });
            return Results.Created($"/api/v1/credentials/{id}", new { id });
        }).RequireAuthorization(Permissions.CredentialsManage);

        g.MapPut("/{id:guid}", async (Guid id, CredentialBody body, NpgsqlDataSource ds, CredentialProtector protector, RequestContext rc, CancellationToken ct) =>
        {
            var err = Validate(body, requireSecret: false);
            if (err is not null) return Validation.Problem(err.Value.Field, err.Value.Message);
            var hasSecret = body.Secret is not null && ToSecret(body.Secret) is var s && (s.Password ?? s.PrivateKey ?? s.Community ?? s.AuthPassword) is not null;
            await using var cmd = ds.CreateCommand($"""
                UPDATE zentriq.credentials SET name = @n, kind = @k, username = @u, allowed_scope = @scope, options = @o, description = @d
                {(hasSecret ? ", secret_protected = @s, rotated_at = now()" : "")}
                 WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            Bind(cmd, body, rc.UserId);
            cmd.Parameters.RemoveAt("by"); // created_by is not changed on update
            if (hasSecret) cmd.Parameters.AddWithValue("s", protector.Protect(id, ToSecret(body.Secret!)));
            int n;
            try { n = await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A credential with this name already exists."); }
            if (n == 0) return Results.NotFound();
            await rc.AuditAsync("credential.update", body.Name, detail: new { body.Kind, body.Username, secretChanged = hasSecret });
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.CredentialsManage);

        g.MapDelete("/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.credentials WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            var name = await cmd.ExecuteScalarAsync(ct) as string;
            if (name is null) return Results.NotFound();
            await rc.AuditAsync("credential.delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.CredentialsManage);

        g.MapGet("/ssh-host-keys", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT host, key_type, fingerprint, first_seen_at, last_seen_at FROM zentriq.ssh_known_hosts ORDER BY host");
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new { host = r.GetString(0), keyType = r.GetString(1), fingerprint = r.GetString(2), firstSeenAt = r.GetFieldValue<DateTimeOffset>(3), lastSeenAt = r.GetFieldValue<DateTimeOffset>(4) });
            return Results.Ok(list);
        }).RequireAuthorization(Permissions.CredentialsManage);

        g.MapDelete("/ssh-host-keys/{host}", async (string host, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.ssh_known_hosts WHERE host = @h");
            cmd.Parameters.AddWithValue("h", host.ToLowerInvariant());
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await rc.AuditAsync("credential.ssh_host_key_cleared", host);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.CredentialsManage);
    }

    private static void Bind(NpgsqlCommand cmd, CredentialBody b, Guid? userId)
    {
        cmd.Parameters.AddWithValue("n", b.Name.Trim());
        cmd.Parameters.AddWithValue("k", b.Kind);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = (object?)b.Username?.Trim() ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("scope", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(b.AllowedScope) ? DBNull.Value : b.AllowedScope.Trim() });
        cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Jsonb) { Value = b.Options is { ValueKind: JsonValueKind.Object } o ? o.GetRawText() : "{}" });
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = (object?)b.Description?.Trim() ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
    }

    private static CredentialSecret ToSecret(SecretBody s) => new()
    {
        Password = Empty(s.Password), PrivateKey = Empty(s.PrivateKey), Passphrase = Empty(s.Passphrase),
        Community = Empty(s.Community), AuthPassword = Empty(s.AuthPassword), PrivPassword = Empty(s.PrivPassword),
    };

    private static string? Empty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static (string Field, string Message)? Validate(CredentialBody b, bool requireSecret)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 100) return ("name", "Enter a name (max 100 characters).");
        if (!Kinds.Contains(b.Kind)) return ("kind", "Unknown credential type.");
        var needsUser = b.Kind is not "snmp_v2c";
        if (needsUser && string.IsNullOrWhiteSpace(b.Username)) return ("username", b.Kind == "snmp_v3" ? "Enter the SNMPv3 user name." : "Enter the user name (DOMAIN\\user, user@domain or a local account).");
        if (b.AllowedScope is { Length: > 2000 }) return ("allowedScope", "Scope list is too long.");
        if (!string.IsNullOrWhiteSpace(b.AllowedScope))
            foreach (var entry in Zentriq.Application.Discovery.TargetParser.SplitEntries(b.AllowedScope))
                if (!Zentriq.Application.Discovery.TargetParser.TryParseCidr(entry.Contains('/') ? entry : entry + "/32", 0, out _, out var e))
                    return ("allowedScope", $"'{entry}': {e}");
        if (!requireSecret && b.Secret is null) return null;
        var s = b.Secret;
        if (s is null) return ("secret", "Enter the secret.");
        return b.Kind switch
        {
            "windows_domain" or "windows_local" or "ssh_password" or "ldap_bind" when string.IsNullOrEmpty(s.Password) && requireSecret => ("secret.password", "Enter the password."),
            "ssh_key" when string.IsNullOrEmpty(s.PrivateKey) && requireSecret => ("secret.privateKey", "Paste the private key (OpenSSH or PEM format)."),
            "ssh_key" when s.PrivateKey is { } k && !k.Contains("PRIVATE KEY") => ("secret.privateKey", "This does not look like a private key."),
            "snmp_v2c" when string.IsNullOrEmpty(s.Community) && requireSecret => ("secret.community", "Enter the SNMP community."),
            "snmp_v3" when string.IsNullOrEmpty(s.AuthPassword) && requireSecret => ("secret.authPassword", "Enter the SNMPv3 authentication password."),
            "snmp_v3" when s.AuthPassword is { Length: < 8 } => ("secret.authPassword", "SNMPv3 passwords must be at least 8 characters."),
            _ => null,
        };
    }
}
