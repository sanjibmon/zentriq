using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.ActiveDirectory;
using Zentriq.Infrastructure.Jobs;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Active Directory connections: DC address and name, port/LDAPS, bind account and password (encrypted, write-only),
/// domain or OU distinguished name. A sync job reads OUs, users, groups and computers into directory_objects.
/// </summary>
public static class DirectoryEndpoints
{
    public sealed record ConnectionBody(string Name, string ServerHost, string? DcName, int? Port, bool? Ldaps, bool? VerifyCertificate,
        string? BaseDn, string Username, string? Password, int? InactiveDays);

    public static void MapDirectoryEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/directory").WithTags("Active Directory");

        g.MapGet("/connections", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT c.id, c.name, c.server_host, c.dc_name, c.port, c.transport, c.verify_certificate, c.base_dn, c.username,
                       c.inactive_days, c.last_sync_at, c.last_sync_status, c.last_error, c.counts::text, c.created_at, c.last_validated_at,
                       (SELECT j.status FROM zentriq.jobs j WHERE j.job_type = 'directory.sync' AND j.payload->>'connectionId' = c.id::text
                         ORDER BY j.created_at DESC LIMIT 1)
                  FROM zentriq.directory_connections c ORDER BY c.name
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            string? S(NpgsqlDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
            DateTimeOffset? D(NpgsqlDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetFieldValue<DateTimeOffset>(i);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), serverHost = S(r, 2), dcName = S(r, 3), port = r.GetInt32(4),
                    ldaps = r.GetString(5) == "ldaps", verifyCertificate = r.GetBoolean(6), baseDn = S(r, 7), username = S(r, 8),
                    inactiveDays = r.GetInt32(9), lastSyncAt = D(r, 10), lastSyncStatus = S(r, 11), lastError = S(r, 12),
                    counts = JsonDocument.Parse(r.GetString(13)).RootElement, createdAt = r.GetFieldValue<DateTimeOffset>(14),
                    lastValidatedAt = D(r, 15), syncJobStatus = S(r, 16), hasPassword = true,
                });
            return Results.Ok(list);
        }).RequireAuthorization(Permissions.ScansRun);

        // Test before saving (password supplied) or test a saved connection (password omitted -> stored one is used).
        g.MapPost("/connections/test", async (ConnectionBody body, Guid? id, DirectoryStore store, CancellationToken ct) =>
        {
            var err = Validate(body with { BaseDn = string.IsNullOrWhiteSpace(body.BaseDn) ? "DC=placeholder" : body.BaseDn }, requirePassword: id is null);
            if (err is not null) return Validation.Problem(err.Value.Field, err.Value.Message);
            var settings = ToSettings(body with { BaseDn = body.BaseDn ?? "" }, body.Password ?? "");
            if (string.IsNullOrEmpty(body.Password) && id is { } existing)
            {
                var saved = await store.LoadAsync(existing, ct);
                if (saved is null) return Results.NotFound();
                settings = settings with { Password = saved.Settings.Password };
            }
            var result = await Task.Run(() => DirectoryClient.Test(settings, TimeSpan.FromSeconds(20)), ct);
            return Results.Ok(result);
        }).RequireAuthorization(Permissions.DirectoryManage);

        g.MapPost("/connections", async (ConnectionBody body, NpgsqlDataSource ds, CredentialProtector protector, RequestContext rc, CancellationToken ct) =>
        {
            var err = Validate(body, requirePassword: true);
            if (err is not null) return Validation.Problem(err.Value.Field, err.Value.Message);
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.directory_connections (id, name, domain_name, servers, transport, server_host, dc_name, port, base_dn, username,
                    secret_protected, verify_certificate, inactive_days, created_by)
                VALUES (@id, @n, @dom, @servers, @t, @h, @dc, @port, @dn, @u, @s, @vc, @days, @by)
                """);
            cmd.Parameters.AddWithValue("id", id);
            Bind(cmd, body);
            cmd.Parameters.AddWithValue("s", protector.Protect(id, new CredentialSecret { Password = body.Password }));
            cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A connection with this name already exists."); }
            await rc.AuditAsync("directory.connection_create", body.Name, detail: new { body.ServerHost, body.DcName, body.BaseDn, body.Username, body.Ldaps });
            return Results.Created($"/api/v1/directory/connections/{id}", new { id });
        }).RequireAuthorization(Permissions.DirectoryManage);

        g.MapPut("/connections/{id:guid}", async (Guid id, ConnectionBody body, NpgsqlDataSource ds, CredentialProtector protector, RequestContext rc, CancellationToken ct) =>
        {
            var err = Validate(body, requirePassword: false);
            if (err is not null) return Validation.Problem(err.Value.Field, err.Value.Message);
            var newPassword = !string.IsNullOrEmpty(body.Password);
            await using var cmd = ds.CreateCommand($"""
                UPDATE zentriq.directory_connections SET name = @n, domain_name = @dom, servers = @servers, transport = @t, server_host = @h, dc_name = @dc,
                       port = @port, base_dn = @dn, username = @u, verify_certificate = @vc, inactive_days = @days
                       {(newPassword ? ", secret_protected = @s" : "")}
                 WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            Bind(cmd, body);
            if (newPassword) cmd.Parameters.AddWithValue("s", protector.Protect(id, new CredentialSecret { Password = body.Password }));
            int n;
            try { n = await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A connection with this name already exists."); }
            if (n == 0) return Results.NotFound();
            await rc.AuditAsync("directory.connection_update", body.Name, detail: new { body.ServerHost, body.DcName, body.BaseDn, body.Username, passwordChanged = newPassword });
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.DirectoryManage);

        g.MapDelete("/connections/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.directory_connections WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            var name = await cmd.ExecuteScalarAsync(ct) as string;
            if (name is null) return Results.NotFound();
            await rc.AuditAsync("directory.connection_delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DirectoryManage);

        g.MapPost("/connections/{id:guid}/sync", async (Guid id, NpgsqlDataSource ds, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            await using var check = ds.CreateCommand("""
                SELECT c.name, EXISTS (SELECT 1 FROM zentriq.jobs j WHERE j.job_type = 'directory.sync' AND j.status IN ('queued','running')
                                         AND j.payload->>'connectionId' = c.id::text)
                  FROM zentriq.directory_connections c WHERE c.id = @id
                """);
            check.Parameters.AddWithValue("id", id);
            await using var r = await check.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            var name = r.GetString(0);
            if (r.GetBoolean(1)) return Results.Conflict(new { detail = "A synchronisation of this connection is already queued or running." });
            await r.DisposeAsync();
            var jobId = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.DirectorySync, new { connectionId = id }, MaxAttempts: 2, TimeoutSeconds: 3600, CreatedBy: rc.UserId), ct: ct);
            await using var mark = ds.CreateCommand("UPDATE zentriq.directory_connections SET last_sync_status = 'queued' WHERE id = @id");
            mark.Parameters.AddWithValue("id", id);
            await mark.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("directory.sync_requested", name);
            return Results.Accepted($"/api/v1/jobs/{jobId}", new { jobId });
        }).RequireAuthorization(Permissions.DirectoryManage);

        g.MapGet("/objects", async (Guid? connectionId, string? type, string? q, int? page, int? pageSize, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var cls = type switch { "computers" => "computer", "users" => "user", "groups" => "group", "ous" => "organizationalUnit", _ => null };
            var size = Math.Clamp(pageSize ?? 50, 1, 500);
            var offset = (Math.Max(page ?? 1, 1) - 1) * size;
            const string where = """
                WHERE (@c::uuid IS NULL OR o.connection_id = @c) AND (@cls::text IS NULL OR o.object_class = @cls)
                  AND (@q::text IS NULL OR o.name ILIKE @q OR o.sam_account_name ILIKE @q OR o.display_name ILIKE @q OR o.dns_host_name ILIKE @q
                       OR o.mail ILIKE @q OR o.user_principal_name ILIKE @q OR o.distinguished_name ILIKE @q)
                """;
            void P(NpgsqlCommand cmd)
            {
                cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Uuid) { Value = (object?)connectionId ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("cls", NpgsqlDbType.Text) { Value = (object?)cls ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : $"%{q.Trim()}%" });
            }
            await using var count = ds.CreateCommand($"SELECT count(*) FROM zentriq.directory_objects o {where}");
            P(count);
            var total = (long)(await count.ExecuteScalarAsync(ct))!;
            await using var cmd = ds.CreateCommand($"""
                SELECT o.id, o.object_class, o.name, o.sam_account_name, o.display_name, o.mail, o.user_principal_name, o.dns_host_name,
                       o.operating_system, o.operating_system_version, o.enabled, o.last_logon_timestamp, o.when_created, o.password_last_set,
                       o.department, o.title, o.description, o.distinguished_name, o.parent_dn, o.member_of, o.synced_at, o.connection_id,
                       (SELECT a.id FROM zentriq.assets a WHERE a.status = 'active' AND o.object_class = 'computer'
                          AND (lower(a.fqdn) = lower(o.dns_host_name) OR lower(a.hostname) = lower(o.name)) LIMIT 1)
                  FROM zentriq.directory_objects o {where}
                 ORDER BY o.object_class, lower(coalesce(o.name, o.distinguished_name)) LIMIT @lim OFFSET @off
                """);
            P(cmd);
            cmd.Parameters.AddWithValue("lim", size);
            cmd.Parameters.AddWithValue("off", offset);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            DateTimeOffset? D(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
            while (await r.ReadAsync(ct))
                items.Add(new
                {
                    id = r.GetGuid(0), objectClass = r.GetString(1), name = S(2), samAccountName = S(3), displayName = S(4), mail = S(5),
                    userPrincipalName = S(6), dnsHostName = S(7), operatingSystem = S(8), operatingSystemVersion = S(9),
                    enabled = r.IsDBNull(10) ? (bool?)null : r.GetBoolean(10), lastLogon = D(11), whenCreated = D(12), passwordLastSet = D(13),
                    department = S(14), title = S(15), description = S(16), distinguishedName = r.GetString(17), parentDn = S(18),
                    memberOf = r.GetFieldValue<string[]>(19), syncedAt = r.GetFieldValue<DateTimeOffset>(20), connectionId = r.GetGuid(21),
                    assetId = r.IsDBNull(22) ? (Guid?)null : r.GetGuid(22),
                });
            return Results.Ok(new { items, total, page = Math.Max(page ?? 1, 1), pageSize = size });
        }).RequireAuthorization(Permissions.ScansRun);
    }

    private static DirectorySettings ToSettings(ConnectionBody b, string password) =>
        new(b.ServerHost.Trim(), string.IsNullOrWhiteSpace(b.DcName) ? null : b.DcName.Trim(), PortOf(b), b.Ldaps ?? true,
            b.VerifyCertificate ?? true, (b.BaseDn ?? "").Trim(), b.Username.Trim(), password);

    private static int PortOf(ConnectionBody b) => b.Port is > 0 and < 65536 ? b.Port.Value : (b.Ldaps ?? true) ? 636 : 389;

    private static void Bind(NpgsqlCommand cmd, ConnectionBody b)
    {
        var ldaps = b.Ldaps ?? true;
        cmd.Parameters.AddWithValue("n", b.Name.Trim());
        var baseDn = (b.BaseDn ?? "").Trim();
        var domain = DirectoryClient.DomainFromDn(baseDn);
        cmd.Parameters.AddWithValue("dom", domain.Length > 0 ? domain : baseDn);
        cmd.Parameters.Add(new NpgsqlParameter("servers", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = new[] { b.ServerHost.Trim() } });
        cmd.Parameters.AddWithValue("t", ldaps ? "ldaps" : "ldap");
        cmd.Parameters.AddWithValue("h", b.ServerHost.Trim());
        cmd.Parameters.Add(new NpgsqlParameter("dc", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(b.DcName) ? DBNull.Value : b.DcName.Trim() });
        cmd.Parameters.AddWithValue("port", PortOf(b));
        cmd.Parameters.AddWithValue("dn", baseDn);
        cmd.Parameters.AddWithValue("u", b.Username.Trim());
        cmd.Parameters.AddWithValue("vc", b.VerifyCertificate ?? true);
        cmd.Parameters.AddWithValue("days", Math.Clamp(b.InactiveDays ?? 90, 0, 3650));
    }

    internal static (string Field, string Message)? Validate(ConnectionBody b, bool requirePassword)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 100) return ("name", "Enter a name (max 100 characters).");
        if (string.IsNullOrWhiteSpace(b.ServerHost) || b.ServerHost.Length > 255 || b.ServerHost.Any(char.IsWhiteSpace))
            return ("serverHost", "Enter the domain controller IP address or host name, e.g. 192.168.1.2 or dc01.corp.example.com.");
        if (b.DcName is { Length: > 255 }) return ("dcName", "Domain controller name is too long.");
        if (b.Port is { } p && (p < 1 || p > 65535)) return ("port", "Port must be 1-65535 (636 for LDAPS, 389 for LDAP).");
        if (string.IsNullOrWhiteSpace(b.BaseDn) || !b.BaseDn.Contains("DC=", StringComparison.OrdinalIgnoreCase) || !b.BaseDn.Contains('='))
            return ("baseDn", "Enter the domain or OU as a distinguished name, e.g. DC=corp,DC=example,DC=com or OU=Servers,DC=corp,DC=example,DC=com.");
        if (string.IsNullOrWhiteSpace(b.Username)) return ("username", "Enter the user ID, e.g. CORP\\svc-zentriq or svc-zentriq@corp.example.com.");
        var isDn = b.Username.TrimStart().StartsWith("CN=", StringComparison.OrdinalIgnoreCase);
        if (isDn && b.Ldaps == false) return ("username", "A distinguished-name user requires LDAPS. Use DOMAIN\\user or user@domain for LDAP (389).");
        if (requirePassword && string.IsNullOrEmpty(b.Password)) return ("password", "Enter the password.");
        if (b.InactiveDays is < 0 or > 3650) return ("inactiveDays", "Inactive days must be 0-3650 (0 = scan all).");
        return null;
    }
}
