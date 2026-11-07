using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Deployment;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// The console side of the tray icon: which packages the Software Center offers (Software Deployment › Self-Service),
/// requests waiting for approval, the tray settings; and the help requests users send (Remote Support › Help Requests).
/// </summary>
public static class SelfServiceEndpoints
{
    public sealed record OfferBody(bool Enabled, bool Approval, string? Description, string? Category, Guid[]? GroupIds);
    public sealed record DecisionBody(bool Approve, string? Note);
    public sealed record HelpStatusBody(string? Status, string? Note);

    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTimeOffset? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    public static void MapSelfServiceEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/deployment/self-service").WithTags("Software deployment").RequireModule(Modules.Deployment).RequireAuthorization(Permissions.DeployView);

        g.MapGet("", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var packages = new List<object>();
            await using (var cmd = ds.CreateCommand("""
                SELECT p.id, p.name, p.version, p.publisher, p.platform, p.size, p.self_service, p.self_service_approval, p.self_service_description,
                       p.self_service_category, p.self_service_group_ids,
                       (SELECT count(*) FROM zentriq.deployment_jobs j WHERE j.package_id = p.id AND j.targets ? 'selfService'),
                       (SELECT count(*) FROM zentriq.self_service_requests r WHERE r.package_id = p.id AND r.status = 'pending')
                  FROM zentriq.deployment_packages p WHERE p.active ORDER BY NOT p.self_service, lower(p.name)
                """))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    packages.Add(new
                    {
                        id = r.GetGuid(0), name = r.GetString(1), version = S(r, 2), publisher = S(r, 3), platform = r.GetString(4), size = r.GetInt64(5), enabled = r.GetBoolean(6),
                        approval = r.GetBoolean(7), description = S(r, 8), category = S(r, 9), groupIds = r.GetFieldValue<Guid[]>(10), installs = r.GetInt64(11), pending = r.GetInt64(12),
                    });
            var requests = new List<object>();
            await using (var cmd = ds.CreateCommand("""
                SELECT q.id, q.package_id, p.name, p.version, q.asset_id, COALESCE(a.hostname, host(a.primary_ip)), q.user_name, q.reason, q.status, q.created_at,
                       q.decided_at, u.email, q.decision_note, x.status
                  FROM zentriq.self_service_requests q
                  JOIN zentriq.deployment_packages p ON p.id = q.package_id
                  JOIN zentriq.assets a ON a.id = q.asset_id
                  LEFT JOIN zentriq.users u ON u.id = q.decided_by
                  LEFT JOIN zentriq.asset_actions x ON x.id = q.action_id
                 ORDER BY q.status <> 'pending', q.created_at DESC LIMIT 500
                """))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    requests.Add(new
                    {
                        id = r.GetGuid(0), packageId = r.GetGuid(1), package = r.GetString(2), version = S(r, 3), assetId = r.GetGuid(4), device = S(r, 5), user = S(r, 6),
                        reason = S(r, 7), status = r.GetString(8), createdAt = T(r, 9), decidedAt = T(r, 10), decidedBy = S(r, 11), note = S(r, 12), install = S(r, 13),
                    });
            return Results.Ok(new { packages, requests, settings = await SelfService.SettingsAsync(ds, ct) });
        });

        g.MapPut("/packages/{id:guid}", async (Guid id, OfferBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.deployment_packages SET self_service = @e, self_service_approval = @ap, self_service_description = @d, self_service_category = @c, self_service_group_ids = @g
                 WHERE id = @id RETURNING name
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("e", body.Enabled);
            cmd.Parameters.AddWithValue("ap", body.Approval);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = (object?)SelfService.Clip(body.Description, 500) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = (object?)SelfService.Clip(body.Category, 60) ?? DBNull.Value });
            cmd.Parameters.AddWithValue("g", (body.GroupIds ?? []).Distinct().ToArray());
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("selfservice.offer", name, detail: new { body.Enabled, body.Approval, body.Category, groups = body.GroupIds?.Length ?? 0 }, ct: ct);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeployManage);

        g.MapPost("/requests/{id:guid}", async (Guid id, DecisionBody body, NpgsqlDataSource ds, ZentriqPaths paths, RequestContext rc, CancellationToken ct) =>
        {
            string? name;
            try { name = await SelfService.DecideAsync(ds, id, body.Approve, body.Note, rc.UserId, p => File.Exists(DeploymentFiles.PathOf(paths, p)), ct); }
            catch (InvalidOperationException ex) { return Validation.Problem("id", ex.Message); }
            if (name is null) return Results.Problem(statusCode: 409, title: "This request was already decided.");
            if (body.Approve) AgentPresence.Instance.Nudge();
            await rc.AuditAsync(body.Approve ? "selfservice.approve" : "selfservice.deny", name, detail: new { requestId = id, body.Note }, ct: ct);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeployRun);

        g.MapPut("/settings", async (TraySettings body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await SelfService.SaveSettingsAsync(ds, body, ct);
            await rc.AuditAsync("tray.settings", null, detail: new { body.Enabled, body.SoftwareCenter, body.RemoteHelp }, ct: ct);
            return Results.Ok(await SelfService.SettingsAsync(ds, ct));
        }).RequireAuthorization(Permissions.SettingsManage);

        // ---- help requests ------------------------------------------------------------------------------------------
        var h = api.MapGroup("/remote/help-requests").WithTags("Remote support").RequireModule(Modules.Remote).RequireAuthorization(Permissions.RemoteStart);

        h.MapGet("", async (string? status, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT h.id, h.number, h.asset_id, COALESCE(a.hostname, host(a.primary_ip)), h.user_name, h.message, h.contact, h.status, u.email, h.note,
                       h.created_at, h.updated_at, h.closed_at,
                       EXISTS (SELECT 1 FROM zentriq.remote_nodes n WHERE n.asset_id = h.asset_id)
                  FROM zentriq.help_requests h JOIN zentriq.assets a ON a.id = h.asset_id LEFT JOIN zentriq.users u ON u.id = h.taken_by
                 WHERE (@s = '' AND h.status <> 'closed') OR h.status = @s OR @s = 'all'
                 ORDER BY h.status = 'closed', h.created_at DESC LIMIT 500
                """);
            cmd.Parameters.AddWithValue("s", status ?? "");
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), number = r.GetInt64(1), assetId = r.GetGuid(2), device = S(r, 3), user = S(r, 4), message = r.GetString(5), contact = S(r, 6),
                    status = r.GetString(7), takenBy = S(r, 8), note = S(r, 9), createdAt = T(r, 10), updatedAt = T(r, 11), closedAt = T(r, 12), remoteReady = r.GetBoolean(13),
                });
            return Results.Ok(list);
        });

        h.MapPost("/{id:guid}", async (Guid id, HelpStatusBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (body.Status is not ("open" or "in_progress" or "closed")) return Validation.Problem("status", "Open, in progress or closed.");
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.help_requests SET status = @s, note = COALESCE(@n, note), updated_at = now(),
                       taken_by = CASE WHEN @s = 'in_progress' THEN @by ELSE taken_by END, closed_at = CASE WHEN @s = 'closed' THEN now() END
                 WHERE id = @id RETURNING number
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("s", body.Status);
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = (object?)SelfService.Clip(body.Note, 1000) ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            if (await cmd.ExecuteScalarAsync(ct) is not long number) return Results.NotFound();
            await rc.AuditAsync("help.status", $"#{number}", detail: new { body.Status }, ct: ct);
            return Results.NoContent();
        });
    }
}
