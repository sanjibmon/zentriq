using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Infrastructure.SecurityPack;
using Zentriq.Inventory;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Security Pack on Windows devices (build 121), for agents with the capability "security-pack":
///   GET  /api/agent/v1/security-pack/policy     the device's policy (everything off without the Security Pack licence)
///   POST /api/agent/v1/security-pack/report     administrators, BitLocker status and recovery passwords, USB devices, events
///   POST /api/agent/v1/security-pack/requests   a user asks to run an application as administrator, or for administrator rights
///   GET  /api/agent/v1/security-pack/requests   the user's recent requests on this device (tray)
/// </summary>
public static partial class AgentEndpoints
{
    private static void MapAgentSecurityPackEndpoints(RouteGroupBuilder g)
    {
        g.MapGet("/security-pack/policy", async (HttpContext http, NpgsqlDataSource ds, AgentServer server, LicensingService licensing, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            var licensed = (await licensing.GetStatusAsync(ct)).ModuleEnabled(Modules.SecurityPack);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var policy = await SecurityPackStore.PolicyForAsync(conn, agent.AssetId, licensed, ct);
            await using (var upd = new NpgsqlCommand("UPDATE zentriq.agent_registrations SET secpack_revision = @r WHERE id = @id", conn))
            {
                upd.Parameters.AddWithValue("r", policy.Revision);
                upd.Parameters.AddWithValue("id", agent.Id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            return Results.Json(policy, SecurityPackStore.Json);
        }).AllowAnonymous();

        g.MapPost("/security-pack/report", async (SecurityPackReport body, HttpContext http, NpgsqlDataSource ds, AgentServer server, CredentialProtector protector, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            if (agent.AssetId is not { } assetId) return Results.Ok(new { stored = false });
            await using var conn = await ds.OpenConnectionAsync(ct);
            await SecurityPackStore.SaveReportAsync(conn, assetId, body, protector, ct);
            if (body.Keys.Count > 0) await AuditAgentAsync(http, agent, "bitlocker.key_escrowed", string.Join(", ", body.Keys.Select(k => $"{k.Volume} {k.ProtectorId}")), new { count = body.Keys.Count }, ct);
            foreach (var e in body.PrivilegeEvents.Where(e => e.Kind is "elevated" or "admin_added" or "admin_removed" or "member_removed").Take(50))
                await AuditAgentAsync(http, agent, "privilege." + e.Kind, e.Path ?? e.User, new { user = e.User, sha256 = e.Sha256, publisher = e.Publisher, detail = e.Detail }, ct);
            return Results.Ok(new { stored = true });
        }).AllowAnonymous();

        g.MapPost("/security-pack/requests", async (PrivilegeRequest body, HttpContext http, NpgsqlDataSource ds, AgentServer server, LicensingService licensing, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            if (agent.AssetId is not { } assetId) return Results.Problem(statusCode: 409, title: "This device is not in the inventory yet.");
            if (!(await licensing.GetStatusAsync(ct)).ModuleEnabled(Modules.SecurityPack)) return Results.Problem(statusCode: 403, title: "Requests are not available.");
            if (body.Kind is not ("elevate" or "admin") || string.IsNullOrWhiteSpace(body.User)) return Results.Problem(statusCode: 400, title: "Not a valid request.");
            if (body.Kind == "elevate" && string.IsNullOrWhiteSpace(body.Path)) return Results.Problem(statusCode: 400, title: "Choose the application.");
            await using var conn = await ds.OpenConnectionAsync(ct);
            // One open request per user and application (or for administrator rights) at a time.
            await using (var dup = new NpgsqlCommand("""
                SELECT id FROM zentriq.epm_requests WHERE asset_id = @a AND lower(user_name) = lower(@u) AND kind = @k AND status = 'pending'
                   AND (@k = 'admin' OR lower(COALESCE(sha256, file_path)) = lower(COALESCE(@h, @p))) LIMIT 1
                """, conn))
            {
                dup.Parameters.AddWithValue("a", assetId);
                dup.Parameters.AddWithValue("u", body.User.Trim());
                dup.Parameters.AddWithValue("k", body.Kind);
                dup.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Text) { Value = (object?)Trunc(body.Sha256, 64) ?? DBNull.Value });
                dup.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = (object?)Trunc(body.Path, 1000) ?? DBNull.Value });
                if (await dup.ExecuteScalarAsync(ct) is Guid existing) return Results.Ok(new { id = existing, status = "pending", duplicate = true });
            }
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.epm_requests (asset_id, user_name, kind, file_path, file_name, sha256, publisher, product, minutes, reason)
                VALUES (@a, @u, @k, @p, @n, @h, @pub, @prod, @m, @r) RETURNING id
                """, conn);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.AddWithValue("u", Trunc(body.User.Trim(), 300)!);
            cmd.Parameters.AddWithValue("k", body.Kind);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = (object?)Trunc(body.Path, 1000) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = (object?)(body.Path is { } path ? Trunc(SecurityPackFileName(path), 260) : null) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Text) { Value = (object?)Trunc(body.Sha256?.ToLowerInvariant(), 64) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("pub", NpgsqlDbType.Text) { Value = (object?)Trunc(body.Publisher, 300) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("prod", NpgsqlDbType.Text) { Value = (object?)Trunc(body.Product, 300) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Integer) { Value = body.Kind == "admin" ? Math.Clamp(body.Minutes ?? 60, 15, 1440) : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Text) { Value = (object?)Trunc(body.Reason, 1000) ?? DBNull.Value });
            var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
            await AuditAgentAsync(http, agent, "privilege.request", body.Kind == "admin" ? $"administrator rights for {body.Minutes} min" : body.Path, new { user = body.User, reason = body.Reason }, ct);
            return Results.Ok(new { id, status = "pending" });
        }).AllowAnonymous();

        g.MapGet("/security-pack/requests", async (string? user, HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            if (agent.AssetId is not { } assetId || string.IsNullOrWhiteSpace(user)) return Results.Ok(Array.Empty<object>());
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await LicenseEndpoints.ScalarAsync(conn, """
                SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."createdAt" DESC), '[]')::text FROM (
                  SELECT id AS "id", kind AS "kind", file_name AS "fileName", minutes AS "minutes", status AS "status", decision AS "decision",
                         created_at AS "createdAt", decided_at AS "decidedAt"
                    FROM zentriq.epm_requests WHERE asset_id = @a AND lower(user_name) = lower(@u) AND created_at > now() - interval '30 days'
                   ORDER BY created_at DESC LIMIT 20) t
                """, c => { c.Parameters.AddWithValue("a", assetId); c.Parameters.AddWithValue("u", user.Trim()); }, ct);
            return Results.Text(json ?? "[]", "application/json");
        }).AllowAnonymous();
    }

    private static string SecurityPackFileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
}
