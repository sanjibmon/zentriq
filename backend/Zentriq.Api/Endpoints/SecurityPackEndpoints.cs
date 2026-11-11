using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Infrastructure.SecurityPack;
using Zentriq.Inventory;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Security Pack (build 122), a separately licensed add-on for Windows devices with the Zentriq Agent: endpoint privilege
/// management, BitLocker management and USB device control. Separate from Security Configuration, which stays read-only.
/// </summary>
public static class SecurityPackEndpoints
{
    public sealed record PrivilegePolicyBody(Guid? GroupId, string? Mode, string[]? AllowedAdmins, bool? Elevation, bool? Requests, int? MaxAdminMinutes);
    public sealed record BitLockerPolicyBody(Guid? GroupId, string? Mode, string? Method, bool? UsedSpaceOnly, bool? FixedDrives, bool? RotateAfterView);
    public sealed record UsbPolicyBody(Guid? GroupId, string? Mode, bool? AuditFiles, string? Message);
    public sealed record RuleBody(string? Name, string? Kind, string? Value, string? FileName, Guid? GroupId, Guid? AssetId, int? Uses, DateTimeOffset? ExpiresAt, bool? Enabled, string? Note);
    public sealed record DecideBody(bool Approve, string? Scope, Guid? GroupId, int? Minutes, string? Note);
    public sealed record GrantBody(Guid AssetId, string? User, int? Minutes, string? Reason);
    public sealed record RevealBody(string? Reason);
    public sealed record AllowBody(string? DeviceId, string? Name, string? Access, Guid? GroupId, string? Note);

    private static readonly string[] Methods = ["XtsAes256", "XtsAes128", "Aes256", "Aes128"];

    private static NpgsqlParameter P(string name, object? value, NpgsqlDbType type) => new(name, type) { Value = value ?? DBNull.Value };
    private static string? T(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();

    private static async Task<IResult> JsonAsync(NpgsqlDataSource ds, string sql, Action<NpgsqlCommand>? bind, CancellationToken ct, string empty = "[]")
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return Results.Text(await LicenseEndpoints.ScalarAsync(conn, sql, bind, ct) ?? empty, "application/json");
    }

    private static async Task<object> PoliciesAsync(NpgsqlDataSource ds, string area, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        return (await SecurityPackStore.PoliciesAsync(conn, area, ct)).Select(p => new { groupId = p.GroupId, groupName = p.GroupName, settings = p.Settings, updatedBy = p.UpdatedBy, updatedAt = p.UpdatedAt });
    }

    private static async Task<IResult?> GroupExistsAsync(NpgsqlDataSource ds, Guid? groupId, CancellationToken ct)
    {
        if (groupId is null) return null;
        await using var cmd = ds.CreateCommand("SELECT count(*) FROM zentriq.device_groups WHERE id = @g");
        cmd.Parameters.AddWithValue("g", groupId.Value);
        return (long)(await cmd.ExecuteScalarAsync(ct))! == 0 ? Validation.Problem("groupId", "The device group does not exist.") : null;
    }

    private static async Task BumpAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await SecurityPackStore.BumpAsync(conn, ct);
    }

    public static void MapSecurityPackEndpoints(this RouteGroupBuilder api)
    {
        var root = api.MapGroup("/security-pack").WithTags("Security Pack").RequireModule(Modules.SecurityPack);
        foreach (var (prefix, perm, map) in new (string, string, Action<RouteGroupBuilder>)[]
                 { ("/privilege", Permissions.PrivilegeView, MapPrivilege), ("/bitlocker", Permissions.BitLockerView, MapBitLocker), ("/usb", Permissions.DeviceControlView, MapUsb) })
        {
            var g = root.MapGroup(prefix).RequireAuthorization(perm);
            map(g);
            // Device groups (of Software Deployment) for the per-group policies.
            g.MapGet("/groups", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, """
                SELECT COALESCE(jsonb_agg(jsonb_build_object('id', id, 'name', name, 'platform', platform) ORDER BY lower(name)), '[]')::text FROM zentriq.device_groups
                """, null, ct));
        }
    }

    private const string Devices = """
        (SELECT count(*) FROM zentriq.agent_registrations r JOIN zentriq.assets a ON a.id = r.asset_id AND a.status = 'active'
          WHERE r.status = 'active' AND 'security-pack' = ANY(COALESCE(r.capabilities, ARRAY[]::text[])))
        """;

    // ---- endpoint privilege management ------------------------------------------------------------------------------------
    private static void MapPrivilege(RouteGroupBuilder g)
    {
        g.MapGet("", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var kpis = await LicenseEndpoints.ScalarAsync(conn, $$"""
                SELECT jsonb_build_object(
                  'devices', {{Devices}},
                  'reporting', (SELECT count(DISTINCT asset_id) FROM zentriq.device_local_admins),
                  'toRemove', (SELECT count(*) FROM zentriq.device_local_admins WHERE status = 'to_remove'),
                  'devicesWithExtraAdmins', (SELECT count(DISTINCT asset_id) FROM zentriq.device_local_admins WHERE status IN ('to_remove')),
                  'removed', (SELECT count(*) FROM zentriq.epm_events WHERE kind = 'member_removed' AND at > now() - interval '30 days'),
                  'elevations', (SELECT count(*) FROM zentriq.epm_events WHERE kind = 'elevated' AND at > now() - interval '7 days'),
                  'refused', (SELECT count(*) FROM zentriq.epm_events WHERE kind = 'refused' AND at > now() - interval '7 days'),
                  'pending', (SELECT count(*) FROM zentriq.epm_requests WHERE status = 'pending'),
                  'grants', (SELECT count(*) FROM zentriq.epm_grants WHERE revoked_at IS NULL AND ends_at > now()),
                  'rules', (SELECT count(*) FROM zentriq.epm_rules WHERE enabled))::text
                """, null, ct);
            return Results.Ok(new { policies = await PoliciesAsync(ds, "privilege", ct), kpis = JsonDocument.Parse(kpis ?? "{}").RootElement });
        });

        g.MapPut("/policy", async (PrivilegePolicyBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (!SecurityPackRules.IsMode("privilege", b.Mode)) return Validation.Problem("mode", "Choose off, audit or enforce.");
            if (await GroupExistsAsync(ds, b.GroupId, ct) is { } bad) return bad;
            var allowed = (b.AllowedAdmins ?? []).Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 300).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (allowed.Count > 100) return Validation.Problem("allowedAdmins", "Use at most 100 accounts and groups.");
            var p = new PrivilegePolicy { Mode = b.Mode!, AllowedAdmins = allowed, Elevation = b.Elevation ?? true, Requests = b.Requests ?? true, MaxAdminMinutes = Math.Clamp(b.MaxAdminMinutes ?? 240, 15, 1440) };
            await using var conn = await ds.OpenConnectionAsync(ct);
            await SecurityPackStore.SavePolicyAsync(conn, "privilege", b.GroupId, new { p.Mode, p.AllowedAdmins, p.Elevation, p.Requests, p.MaxAdminMinutes }, rc.UserName, ct);
            await rc.AuditAsync("privilege.policy", b.GroupId?.ToString() ?? "all devices", detail: p);
            return Results.Ok(p);
        }).RequireAuthorization(Permissions.PrivilegeManage);

        g.MapDelete("/policy/{groupId:guid}", async (Guid groupId, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            if (!await SecurityPackStore.DeletePolicyAsync(conn, "privilege", groupId, ct)) return Results.NotFound();
            await rc.AuditAsync("privilege.policy_delete", groupId.ToString());
            return Results.NoContent();
        }).RequireAuthorization(Permissions.PrivilegeManage);

        g.MapGet("/admins", async (NpgsqlDataSource ds, string? status, string? q, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY lower(t."hostname"), lower(t."member")), '[]')::text FROM (
              SELECT a.id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", m.member AS "member", m.sid AS "sid", m.kind AS "kind",
                     m.status AS "status", m.reported_at AS "reportedAt"
                FROM zentriq.device_local_admins m JOIN zentriq.assets a ON a.id = m.asset_id AND a.status = 'active'
               WHERE (@s::text IS NULL OR m.status = @s) AND (@q::text IS NULL OR a.hostname ILIKE '%' || @q || '%' OR m.member ILIKE '%' || @q || '%')
               LIMIT 5000) t
            """, c => { c.Parameters.Add(P("s", T(status, 20), NpgsqlDbType.Text)); c.Parameters.Add(P("q", T(q, 100), NpgsqlDbType.Text)); }, ct));

        // Rules: applications standard users may run as administrator.
        g.MapGet("/rules", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."createdAt" DESC), '[]')::text FROM (
              SELECT r.id AS "id", r.name AS "name", r.kind AS "kind", r.value AS "value", r.file_name AS "fileName", r.group_id AS "groupId", g.name AS "groupName",
                     r.asset_id AS "assetId", a.hostname AS "hostname", r.uses_left AS "usesLeft", r.expires_at AS "expiresAt", r.enabled AS "enabled", r.note AS "note",
                     r.created_by AS "createdBy", r.created_at AS "createdAt",
                     (SELECT count(*) FROM zentriq.epm_events e WHERE e.rule_id = r.id AND e.kind = 'elevated') AS "used"
                FROM zentriq.epm_rules r LEFT JOIN zentriq.device_groups g ON g.id = r.group_id LEFT JOIN zentriq.assets a ON a.id = r.asset_id) t
            """, null, ct));

        g.MapPost("/rules", async (RuleBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (ValidateRule(b) is { } err) return err;
            if (await GroupExistsAsync(ds, b.GroupId, ct) is { } bad) return bad;
            await using var conn = await ds.OpenConnectionAsync(ct);
            var id = await InsertRuleAsync(conn, b, rc.UserName, ct);
            await SecurityPackStore.BumpAsync(conn, ct);
            await rc.AuditAsync("privilege.rule_create", b.Name, detail: b);
            return Results.Created($"/api/v1/security-pack/privilege/rules/{id}", new { id });
        }).RequireAuthorization(Permissions.PrivilegeManage);

        g.MapPut("/rules/{id:guid}", async (Guid id, RuleBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (ValidateRule(b) is { } err) return err;
            if (await GroupExistsAsync(ds, b.GroupId, ct) is { } bad) return bad;
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                UPDATE zentriq.epm_rules SET name = @n, kind = @k, value = @v, file_name = @f, group_id = @g, asset_id = @a, uses_left = @u, expires_at = @e, enabled = @on, note = @no
                 WHERE id = @id
                """, conn);
            BindRule(cmd, b);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await SecurityPackStore.BumpAsync(conn, ct);
            await rc.AuditAsync("privilege.rule_update", b.Name, detail: b);
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.PrivilegeManage);

        g.MapDelete("/rules/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("DELETE FROM zentriq.epm_rules WHERE id = @id RETURNING name", conn);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await SecurityPackStore.BumpAsync(conn, ct);
            await rc.AuditAsync("privilege.rule_delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.PrivilegeManage);

        g.MapGet("/requests", async (NpgsqlDataSource ds, string? status, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."createdAt" DESC), '[]')::text FROM (
              SELECT q.id AS "id", q.asset_id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", q.user_name AS "user", q.kind AS "kind",
                     q.file_path AS "path", q.file_name AS "fileName", q.sha256 AS "sha256", q.publisher AS "publisher", q.product AS "product", q.minutes AS "minutes",
                     q.reason AS "reason", q.status AS "status", q.decided_by AS "decidedBy", q.decided_at AS "decidedAt", q.decision AS "decision", q.created_at AS "createdAt"
                FROM zentriq.epm_requests q JOIN zentriq.assets a ON a.id = q.asset_id
               WHERE (@s::text IS NULL OR q.status = @s) LIMIT 1000) t
            """, c => c.Parameters.Add(P("s", status is "pending" or "approved" or "denied" ? status : null, NpgsqlDbType.Text)), ct));

        g.MapPost("/requests/{id:guid}", async (Guid id, DecideBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            Guid assetId; string user, kind; string? path, sha, publisher, fileName; int? minutes;
            await using (var get = new NpgsqlCommand("SELECT asset_id, user_name, kind, file_path, sha256, publisher, file_name, minutes FROM zentriq.epm_requests WHERE id = @id AND status = 'pending'", conn))
            {
                get.Parameters.AddWithValue("id", id);
                await using var r = await get.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                (assetId, user, kind) = (r.GetGuid(0), r.GetString(1), r.GetString(2));
                path = r.IsDBNull(3) ? null : r.GetString(3); sha = r.IsDBNull(4) ? null : r.GetString(4); publisher = r.IsDBNull(5) ? null : r.GetString(5);
                fileName = r.IsDBNull(6) ? null : r.GetString(6); minutes = r.IsDBNull(7) ? null : r.GetInt32(7);
            }
            Guid? ruleId = null;
            string decision;
            if (!b.Approve) decision = string.IsNullOrWhiteSpace(b.Note) ? "Not approved" : b.Note.Trim();
            else if (kind == "admin")
            {
                var m = Math.Clamp(b.Minutes ?? minutes ?? 60, 15, 1440);
                await using var gr = new NpgsqlCommand("""
                    INSERT INTO zentriq.epm_grants (asset_id, user_name, ends_at, request_id, reason, created_by) VALUES (@a, @u, now() + make_interval(mins => @m), @r, @why, @by)
                    """, conn);
                gr.Parameters.AddWithValue("a", assetId);
                gr.Parameters.AddWithValue("u", user);
                gr.Parameters.AddWithValue("m", m);
                gr.Parameters.AddWithValue("r", id);
                gr.Parameters.Add(P("why", T(b.Note, 500), NpgsqlDbType.Text));
                gr.Parameters.Add(P("by", rc.UserName, NpgsqlDbType.Text));
                await gr.ExecuteNonQueryAsync(ct);
                decision = $"Administrator rights for {m} minutes";
            }
            else
            {
                var scope = b.Scope is "device" or "all" or "group" ? b.Scope : "once";
                if (scope == "group" && await GroupExistsAsync(ds, b.GroupId, ct) is { } bad) return bad;
                var rule = new RuleBody(fileName ?? "Application", sha is not null ? "hash" : "path", sha ?? path, null,
                    scope == "group" ? b.GroupId : null, scope is "once" or "device" ? assetId : null, scope == "once" ? 1 : null, null, true,
                    $"Approved request of {user}" + (string.IsNullOrWhiteSpace(b.Note) ? "" : ": " + b.Note.Trim()));
                ruleId = await InsertRuleAsync(conn, rule, rc.UserName, ct);
                decision = scope switch { "once" => "Approved once on this device", "device" => "Approved on this device", "group" => "Approved for the device group", _ => "Approved for every device" };
            }
            await using (var upd = new NpgsqlCommand("UPDATE zentriq.epm_requests SET status = @s, decided_by = @by, decided_at = now(), decision = @d, rule_id = @r WHERE id = @id", conn))
            {
                upd.Parameters.AddWithValue("s", b.Approve ? "approved" : "denied");
                upd.Parameters.Add(P("by", rc.UserName, NpgsqlDbType.Text));
                upd.Parameters.AddWithValue("d", decision);
                upd.Parameters.Add(P("r", ruleId, NpgsqlDbType.Uuid));
                upd.Parameters.AddWithValue("id", id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            await SecurityPackStore.BumpAsync(conn, ct);
            await rc.AuditAsync(b.Approve ? "privilege.request_approve" : "privilege.request_deny", kind == "admin" ? $"{user}: administrator rights" : $"{user}: {fileName}", detail: new { decision, sha256 = sha, publisher });
            return Results.Ok(new { decision });
        }).RequireAuthorization(Permissions.PrivilegeManage);

        g.MapGet("/grants", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."active" DESC, t."endsAt" DESC), '[]')::text FROM (
              SELECT x.id AS "id", x.asset_id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", x.user_name AS "user", x.starts_at AS "startsAt",
                     x.ends_at AS "endsAt", x.revoked_at AS "revokedAt", x.revoked_by AS "revokedBy", x.reason AS "reason", x.created_by AS "createdBy",
                     (x.revoked_at IS NULL AND x.ends_at > now()) AS "active"
                FROM zentriq.epm_grants x JOIN zentriq.assets a ON a.id = x.asset_id WHERE x.created_at > now() - interval '90 days' LIMIT 1000) t
            """, null, ct));

        g.MapPost("/grants", async (GrantBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(b.User) || b.User.Length > 300) return Validation.Problem("user", "Enter the user (DOMAIN\\name or name).");
            var m = Math.Clamp(b.Minutes ?? 60, 15, 1440);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.epm_grants (asset_id, user_name, ends_at, reason, created_by)
                SELECT id, @u, now() + make_interval(mins => @m), @why, @by FROM zentriq.assets WHERE id = @a RETURNING id
                """, conn);
            cmd.Parameters.AddWithValue("a", b.AssetId);
            cmd.Parameters.AddWithValue("u", b.User.Trim());
            cmd.Parameters.AddWithValue("m", m);
            cmd.Parameters.Add(P("why", T(b.Reason, 500), NpgsqlDbType.Text));
            cmd.Parameters.Add(P("by", rc.UserName, NpgsqlDbType.Text));
            if (await cmd.ExecuteScalarAsync(ct) is not Guid id) return Results.NotFound();
            await SecurityPackStore.BumpAsync(conn, ct);
            await rc.AuditAsync("privilege.grant", $"{b.User.Trim()} for {m} min", detail: new { assetId = b.AssetId, b.Reason });
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.PrivilegeManage);

        g.MapDelete("/grants/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("UPDATE zentriq.epm_grants SET revoked_at = now(), revoked_by = @by WHERE id = @id AND revoked_at IS NULL AND ends_at > now() RETURNING user_name", conn);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.Add(P("by", rc.UserName, NpgsqlDbType.Text));
            if (await cmd.ExecuteScalarAsync(ct) is not string user) return Results.NotFound();
            await SecurityPackStore.BumpAsync(conn, ct);
            await rc.AuditAsync("privilege.grant_revoke", user);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.PrivilegeManage);

        g.MapGet("/events", async (NpgsqlDataSource ds, string? kind, string? q, Guid? assetId, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."at" DESC), '[]')::text FROM (
              SELECT e.id AS "id", e.at AS "at", e.asset_id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", e.user_name AS "user", e.kind AS "kind",
                     e.file_path AS "path", e.sha256 AS "sha256", e.publisher AS "publisher", e.rule_id AS "ruleId", r.name AS "ruleName", e.detail AS "detail"
                FROM zentriq.epm_events e JOIN zentriq.assets a ON a.id = e.asset_id LEFT JOIN zentriq.epm_rules r ON r.id = e.rule_id
               WHERE (@k::text IS NULL OR e.kind = @k) AND (@as::uuid IS NULL OR e.asset_id = @as)
                 AND (@q::text IS NULL OR a.hostname ILIKE '%' || @q || '%' OR e.user_name ILIKE '%' || @q || '%' OR e.file_path ILIKE '%' || @q || '%')
               ORDER BY e.at DESC LIMIT 2000) t
            """, c => { c.Parameters.Add(P("k", T(kind, 30), NpgsqlDbType.Text)); c.Parameters.Add(P("q", T(q, 100), NpgsqlDbType.Text)); c.Parameters.Add(P("as", assetId, NpgsqlDbType.Uuid)); }, ct));
    }

    private static IResult? ValidateRule(RuleBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 200) return Validation.Problem("name", "Enter a name.");
        if (b.Kind is not ("hash" or "publisher" or "path")) return Validation.Problem("kind", "Choose file hash, publisher or path.");
        var v = b.Value?.Trim() ?? "";
        if (b.Kind == "hash" && !System.Text.RegularExpressions.Regex.IsMatch(v, "^[0-9a-fA-F]{64}$")) return Validation.Problem("value", "Enter the SHA-256 hash of the file (64 hexadecimal characters).");
        if (b.Kind == "publisher" && v.Trim('*', '?').Length < 3) return Validation.Problem("value", "Enter the publisher as it appears in the file's signature, e.g. Microsoft Corporation.");
        if (b.Kind == "path" && (v.Length < 4 || !v.Contains('\\') || v.StartsWith('*'))) return Validation.Problem("value", "Enter the full path, e.g. C:\\Program Files\\Vendor\\tool.exe (wildcards allowed after the folder).");
        if (v.Length > 500) return Validation.Problem("value", "Too long.");
        if (b.Uses is < 1 or > 1000) return Validation.Problem("uses", "Choose 1 to 1000 uses, or leave it empty for no limit.");
        return null;
    }

    private static void BindRule(NpgsqlCommand cmd, RuleBody b)
    {
        cmd.Parameters.AddWithValue("n", b.Name!.Trim());
        cmd.Parameters.AddWithValue("k", b.Kind!);
        cmd.Parameters.AddWithValue("v", b.Kind == "hash" ? b.Value!.Trim().ToLowerInvariant() : b.Value!.Trim());
        cmd.Parameters.Add(P("f", T(b.FileName, 260), NpgsqlDbType.Text));
        cmd.Parameters.Add(P("g", b.GroupId, NpgsqlDbType.Uuid));
        cmd.Parameters.Add(P("a", b.AssetId, NpgsqlDbType.Uuid));
        cmd.Parameters.Add(P("u", b.Uses, NpgsqlDbType.Integer));
        cmd.Parameters.Add(P("e", b.ExpiresAt?.ToUniversalTime(), NpgsqlDbType.TimestampTz));
        cmd.Parameters.AddWithValue("on", b.Enabled ?? true);
        cmd.Parameters.Add(P("no", T(b.Note, 1000), NpgsqlDbType.Text));
    }

    private static async Task<Guid> InsertRuleAsync(NpgsqlConnection conn, RuleBody b, string? by, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.epm_rules (name, kind, value, file_name, group_id, asset_id, uses_left, expires_at, enabled, note, created_by)
            VALUES (@n, @k, @v, @f, @g, @a, @u, @e, @on, @no, @by) RETURNING id
            """, conn);
        BindRule(cmd, b);
        cmd.Parameters.Add(P("by", by, NpgsqlDbType.Text));
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // ---- BitLocker ------------------------------------------------------------------------------------------------------
    private static void MapBitLocker(RouteGroupBuilder g)
    {
        g.MapGet("", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var kpis = await LicenseEndpoints.ScalarAsync(conn, $$"""
                WITH os AS (SELECT v.* FROM zentriq.bitlocker_volumes v JOIN zentriq.assets a ON a.id = v.asset_id AND a.status = 'active' WHERE v.kind = 'os')
                SELECT jsonb_build_object(
                  'devices', {{Devices}},
                  'reporting', (SELECT count(*) FROM os),
                  'protected', (SELECT count(*) FROM os WHERE protection = 'on'),
                  'encrypting', (SELECT count(*) FROM os WHERE status = 'encrypting'),
                  'unprotected', (SELECT count(*) FROM os WHERE COALESCE(protection, 'off') <> 'on' AND COALESCE(status, '') <> 'encrypting'),
                  'noTpm', (SELECT count(*) FROM os WHERE tpm IN ('absent','not_ready')),
                  'errors', (SELECT count(*) FROM os WHERE error IS NOT NULL),
                  'keys', (SELECT count(*) FROM zentriq.bitlocker_keys WHERE retired_at IS NULL),
                  'withoutKey', (SELECT count(*) FROM os WHERE protection = 'on' AND NOT EXISTS (SELECT 1 FROM zentriq.bitlocker_keys k WHERE k.asset_id = os.asset_id AND upper(k.volume) = upper(os.volume) AND k.retired_at IS NULL)))::text
                """, null, ct);
            return Results.Ok(new { policies = await PoliciesAsync(ds, "bitlocker", ct), kpis = JsonDocument.Parse(kpis ?? "{}").RootElement });
        });

        g.MapPut("/policy", async (BitLockerPolicyBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (!SecurityPackRules.IsMode("bitlocker", b.Mode)) return Validation.Problem("mode", "Choose off, report or enforce.");
            if (b.Method is not null && !Methods.Contains(b.Method)) return Validation.Problem("method", "Choose XTS-AES 256, XTS-AES 128, AES-CBC 256 or AES-CBC 128.");
            if (await GroupExistsAsync(ds, b.GroupId, ct) is { } bad) return bad;
            var p = new { mode = b.Mode, method = b.Method ?? "XtsAes256", usedSpaceOnly = b.UsedSpaceOnly ?? true, fixedDrives = b.FixedDrives ?? false, rotateAfterView = b.RotateAfterView ?? true };
            await using var conn = await ds.OpenConnectionAsync(ct);
            await SecurityPackStore.SavePolicyAsync(conn, "bitlocker", b.GroupId, p, rc.UserName, ct);
            await rc.AuditAsync("bitlocker.policy", b.GroupId?.ToString() ?? "all devices", detail: p);
            return Results.Ok(p);
        }).RequireAuthorization(Permissions.BitLockerManage);

        g.MapDelete("/policy/{groupId:guid}", async (Guid groupId, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            if (!await SecurityPackStore.DeletePolicyAsync(conn, "bitlocker", groupId, ct)) return Results.NotFound();
            await rc.AuditAsync("bitlocker.policy_delete", groupId.ToString());
            return Results.NoContent();
        }).RequireAuthorization(Permissions.BitLockerManage);

        g.MapGet("/volumes", async (NpgsqlDataSource ds, string? filter, string? q, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY lower(t."hostname"), t."volume"), '[]')::text FROM (
              SELECT a.id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", v.volume AS "volume", v.kind AS "kind", v.protection AS "protection",
                     v.status AS "status", v.percent AS "percent", v.method AS "method", v.protectors AS "protectors", v.tpm AS "tpm", v.error AS "error",
                     v.reported_at AS "reportedAt", dep.name AS "department",
                     (SELECT count(*) FROM zentriq.bitlocker_keys k WHERE k.asset_id = v.asset_id AND upper(k.volume) = upper(v.volume) AND k.retired_at IS NULL) AS "keys"
                FROM zentriq.bitlocker_volumes v JOIN zentriq.assets a ON a.id = v.asset_id AND a.status = 'active'
                LEFT JOIN zentriq.departments dep ON dep.id = a.department_id
               WHERE (@f::text IS NULL OR (@f = 'unprotected' AND COALESCE(v.protection, 'off') <> 'on') OR (@f = 'protected' AND v.protection = 'on')
                      OR (@f = 'notpm' AND v.tpm IN ('absent','not_ready')) OR (@f = 'errors' AND v.error IS NOT NULL) OR (@f = 'os' AND v.kind = 'os'))
                 AND (@q::text IS NULL OR a.hostname ILIKE '%' || @q || '%')
               LIMIT 5000) t
            """, c => { c.Parameters.Add(P("f", T(filter, 20), NpgsqlDbType.Text)); c.Parameters.Add(P("q", T(q, 100), NpgsqlDbType.Text)); }, ct));

        // Recovery keys: found by the key ID the recovery screen shows (its first characters are enough) or by device.
        g.MapGet("/keys", async (NpgsqlDataSource ds, string? q, Guid? assetId, CancellationToken ct) =>
        {
            var term = T(q, 100)?.Trim('{', '}');
            if (term is null && assetId is null) return Results.Text("[]", "application/json");
            return await JsonAsync(ds, """
                SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."retiredAt" NULLS FIRST, t."escrowedAt" DESC), '[]')::text FROM (
                  SELECT k.id AS "id", k.asset_id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", k.volume AS "volume", k.protector_id AS "keyId",
                         k.escrowed_at AS "escrowedAt", k.retired_at AS "retiredAt", k.viewed_at AS "viewedAt", k.viewed_by AS "viewedBy", k.rotate_requested_at AS "rotateRequestedAt"
                    FROM zentriq.bitlocker_keys k JOIN zentriq.assets a ON a.id = k.asset_id
                   WHERE (@as::uuid IS NULL OR k.asset_id = @as)
                     AND (@q::text IS NULL OR upper(k.protector_id) LIKE upper(@q) || '%' OR a.hostname ILIKE '%' || @q || '%')
                   LIMIT 200) t
                """, c => { c.Parameters.Add(P("q", term, NpgsqlDbType.Text)); c.Parameters.Add(P("as", assetId, NpgsqlDbType.Uuid)); }, ct);
        });

        g.MapPost("/keys/{id:guid}/reveal", async (Guid id, RevealBody b, NpgsqlDataSource ds, RequestContext rc, CredentialProtector protector, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(b.Reason) || b.Reason.Trim().Length < 5) return Validation.Problem("reason", "Enter why the key is needed (e.g. the ticket number).");
            await using var conn = await ds.OpenConnectionAsync(ct);
            var key = await SecurityPackStore.RevealAsync(conn, id, protector, ct);
            if (key is null) return Results.NotFound();
            string? host;
            await using (var h = new NpgsqlCommand("SELECT hostname FROM zentriq.assets WHERE id = @a", conn)) { h.Parameters.AddWithValue("a", key.Value.AssetId); host = await h.ExecuteScalarAsync(ct) as string; }
            // Shown once: with "replace after it was shown" (default) the device gets a new recovery password at its next contact.
            var policy = await SecurityPackStore.PolicyForAsync(conn, key.Value.AssetId, true, ct);
            await using (var upd = new NpgsqlCommand($"""
                UPDATE zentriq.bitlocker_keys SET viewed_at = now(), viewed_by = @by{(policy.BitLocker.RotateAfterView ? ", rotate_requested_at = COALESCE(rotate_requested_at, now())" : "")} WHERE id = @id
                """, conn))
            {
                upd.Parameters.AddWithValue("id", id);
                upd.Parameters.Add(P("by", rc.UserName, NpgsqlDbType.Text));
                await upd.ExecuteNonQueryAsync(ct);
            }
            if (policy.BitLocker.RotateAfterView) await SecurityPackStore.BumpAsync(conn, ct);
            await rc.AuditAsync("bitlocker.key_view", $"{host} {key.Value.Volume} {key.Value.ProtectorId}", detail: new { reason = b.Reason.Trim(), rotate = policy.BitLocker.RotateAfterView });
            return Results.Ok(new { password = key.Value.Password, keyId = key.Value.ProtectorId, volume = key.Value.Volume, hostname = host, rotates = policy.BitLocker.RotateAfterView });
        }).RequireAuthorization(Permissions.BitLockerRecover);

        g.MapPost("/keys/{id:guid}/rotate", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("UPDATE zentriq.bitlocker_keys SET rotate_requested_at = now() WHERE id = @id AND retired_at IS NULL RETURNING protector_id", conn);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string pid) return Results.NotFound();
            await SecurityPackStore.BumpAsync(conn, ct);
            await rc.AuditAsync("bitlocker.key_rotate", pid);
            return Results.Ok(new { rotating = pid });
        }).RequireAuthorization(Permissions.BitLockerManage);
    }

    // ---- USB device control ----------------------------------------------------------------------------------------------
    private static void MapUsb(RouteGroupBuilder g)
    {
        g.MapGet("", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var kpis = await LicenseEndpoints.ScalarAsync(conn, $$"""
                SELECT jsonb_build_object(
                  'devices', {{Devices}},
                  'usbDevices', (SELECT count(DISTINCT device_id) FROM zentriq.usb_devices WHERE last_seen > now() - interval '30 days'),
                  'blocked', (SELECT count(*) FROM zentriq.usb_events WHERE kind = 'blocked' AND at > now() - interval '7 days'),
                  'readonly', (SELECT count(*) FROM zentriq.usb_events WHERE kind = 'readonly' AND at > now() - interval '7 days'),
                  'files', (SELECT count(*) FROM zentriq.usb_events WHERE kind = 'file_written' AND at > now() - interval '7 days'),
                  'filesBytes', (SELECT COALESCE(sum(size), 0) FROM zentriq.usb_events WHERE kind = 'file_written' AND at > now() - interval '7 days'),
                  'allowed', (SELECT count(*) FROM zentriq.usb_allowed))::text
                """, null, ct);
            return Results.Ok(new { policies = await PoliciesAsync(ds, "usb", ct), kpis = JsonDocument.Parse(kpis ?? "{}").RootElement });
        });

        g.MapPut("/policy", async (UsbPolicyBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (!SecurityPackRules.IsMode("usb", b.Mode)) return Validation.Problem("mode", "Choose off, audit, read-only or block.");
            if (await GroupExistsAsync(ds, b.GroupId, ct) is { } bad) return bad;
            var p = new { mode = b.Mode, auditFiles = b.AuditFiles ?? true, message = T(b.Message, 500) };
            await using var conn = await ds.OpenConnectionAsync(ct);
            await SecurityPackStore.SavePolicyAsync(conn, "usb", b.GroupId, p, rc.UserName, ct);
            await rc.AuditAsync("devicecontrol.policy", b.GroupId?.ToString() ?? "all devices", detail: p);
            return Results.Ok(p);
        }).RequireAuthorization(Permissions.DeviceControlManage);

        g.MapDelete("/policy/{groupId:guid}", async (Guid groupId, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            if (!await SecurityPackStore.DeletePolicyAsync(conn, "usb", groupId, ct)) return Results.NotFound();
            await rc.AuditAsync("devicecontrol.policy_delete", groupId.ToString());
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeviceControlManage);

        g.MapGet("/allowed", async (NpgsqlDataSource ds, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."createdAt" DESC), '[]')::text FROM (
              SELECT u.id AS "id", u.device_id AS "deviceId", u.name AS "name", u.access AS "access", u.group_id AS "groupId", g.name AS "groupName", u.note AS "note",
                     u.created_by AS "createdBy", u.created_at AS "createdAt"
                FROM zentriq.usb_allowed u LEFT JOIN zentriq.device_groups g ON g.id = u.group_id) t
            """, null, ct));

        g.MapPost("/allowed", async (AllowBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var id = b.DeviceId?.Trim() ?? "";
            if (id.Length is < 4 or > 400 || id.Trim('*', '?').Length < 4) return Validation.Problem("deviceId", "Enter the device ID: VID_xxxx&PID_yyyy, VID_xxxx&PID_yyyy\\serial number, or the device instance ID.");
            if (b.Access is not (null or "full" or "readonly")) return Validation.Problem("access", "Choose full access or read-only.");
            if (await GroupExistsAsync(ds, b.GroupId, ct) is { } bad) return bad;
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.usb_allowed (device_id, name, access, group_id, note, created_by) VALUES (@d, @n, @x, @g, @no, @by) RETURNING id
                """, conn);
            cmd.Parameters.AddWithValue("d", id);
            cmd.Parameters.Add(P("n", T(b.Name, 200), NpgsqlDbType.Text));
            cmd.Parameters.AddWithValue("x", b.Access ?? "full");
            cmd.Parameters.Add(P("g", b.GroupId, NpgsqlDbType.Uuid));
            cmd.Parameters.Add(P("no", T(b.Note, 500), NpgsqlDbType.Text));
            cmd.Parameters.Add(P("by", rc.UserName, NpgsqlDbType.Text));
            var newId = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            await SecurityPackStore.BumpAsync(conn, ct);
            await rc.AuditAsync("devicecontrol.allow", id, detail: b);
            return Results.Ok(new { id = newId });
        }).RequireAuthorization(Permissions.DeviceControlManage);

        g.MapDelete("/allowed/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand("DELETE FROM zentriq.usb_allowed WHERE id = @id RETURNING device_id", conn);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string d) return Results.NotFound();
            await SecurityPackStore.BumpAsync(conn, ct);
            await rc.AuditAsync("devicecontrol.allow_delete", d);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeviceControlManage);

        g.MapGet("/devices", async (NpgsqlDataSource ds, string? q, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."lastSeen" DESC), '[]')::text FROM (
              SELECT d.asset_id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", d.device_id AS "deviceId", d.name AS "name", d.vid_pid AS "vidPid",
                     d.serial AS "serial", d.first_seen AS "firstSeen", d.last_seen AS "lastSeen", d.last_access AS "access",
                     (SELECT count(*) FROM zentriq.usb_events e WHERE e.asset_id = d.asset_id AND e.device_id = d.device_id AND e.kind = 'file_written') AS "files"
                FROM zentriq.usb_devices d JOIN zentriq.assets a ON a.id = d.asset_id
               WHERE (@q::text IS NULL OR a.hostname ILIKE '%' || @q || '%' OR d.name ILIKE '%' || @q || '%' OR d.device_id ILIKE '%' || @q || '%')
               LIMIT 5000) t
            """, c => c.Parameters.Add(P("q", T(q, 100), NpgsqlDbType.Text)), ct));

        g.MapGet("/events", async (NpgsqlDataSource ds, string? kind, string? q, int? days, Guid? assetId, CancellationToken ct) => await JsonAsync(ds, """
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."at" DESC), '[]')::text FROM (
              SELECT e.id AS "id", e.at AS "at", e.asset_id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", e.user_name AS "user", e.kind AS "kind",
                     e.device_id AS "deviceId", e.device_name AS "deviceName", e.file_path AS "path", e.size AS "size", e.detail AS "detail"
                FROM zentriq.usb_events e JOIN zentriq.assets a ON a.id = e.asset_id
               WHERE e.at > now() - make_interval(days => @d) AND (@as::uuid IS NULL OR e.asset_id = @as)
                 AND (@k::text IS NULL OR e.kind = @k OR (@k = 'files' AND e.kind LIKE 'file_%') OR (@k = 'devices' AND e.kind NOT LIKE 'file_%'))
                 AND (@q::text IS NULL OR a.hostname ILIKE '%' || @q || '%' OR e.user_name ILIKE '%' || @q || '%' OR e.file_path ILIKE '%' || @q || '%' OR e.device_name ILIKE '%' || @q || '%')
               ORDER BY e.at DESC LIMIT 5000) t
            """, c =>
        {
            c.Parameters.AddWithValue("d", Math.Clamp(days ?? 30, 1, 120));
            c.Parameters.Add(P("k", T(kind, 30), NpgsqlDbType.Text));
            c.Parameters.Add(P("q", T(q, 100), NpgsqlDbType.Text));
            c.Parameters.Add(P("as", assetId, NpgsqlDbType.Uuid));
        }, ct));
    }
}
