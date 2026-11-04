using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Infrastructure.Agents;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Deployment;
using Zentriq.Inventory;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Software Deployment on the agent port (client certificate required, like every agent call):
///   GET  /api/agent/v1/app-control/policy     the device's application control policy (approved list, mode, message, WDAC)
///   POST /api/agent/v1/app-control/events     installations blocked or audited, unapproved software found
///   POST /api/agent/v1/app-control/requests   "Request approval" from the message on the device
///   GET  /api/agent/v1/deploy/{packageId}     a package the device was asked to install
/// </summary>
public static partial class AgentEndpoints
{
    public sealed record AppEvent(string? Kind, DateTimeOffset? At, string? Name, string? Publisher, string? Version, string? Sha256, string? Path, string? User, string? Detail);
    public sealed record AppEventsBody(List<AppEvent>? Events);
    public sealed record AppRequestBody(string? Name, string? Publisher, string? Version, string? Sha256, string? Path, string? User, string? Reason);

    private static void MapAgentDeploymentEndpoints(RouteGroupBuilder g)
    {
        g.MapGet("/app-control/policy", async (HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            var platform = await PlatformOfAsync(ds, agent.Id, ct);
            var policy = await AppControlStore.PolicyForAsync(ds, agent.AssetId, platform, ct);
            // The agent has this revision now (it stops being woken for it, even before its next check-in).
            await using var upd = ds.CreateCommand("UPDATE zentriq.agent_registrations SET app_policy_revision = @r WHERE id = @id");
            upd.Parameters.AddWithValue("r", policy.Revision);
            upd.Parameters.AddWithValue("id", agent.Id);
            await upd.ExecuteNonQueryAsync(ct);
            return Results.Json(policy);
        }).AllowAnonymous();

        g.MapPost("/app-control/events", async (AppEventsBody body, HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            var platform = AppControlStore.Family(await PlatformOfAsync(ds, agent.Id, ct));
            var stored = 0;
            foreach (var e in (body.Events ?? []).Take(200))
            {
                var kind = e.Kind is "blocked" or "audited" or "detected" or "wdac" ? e.Kind : null;
                var name = Trunc(e.Name, 300);
                if (kind is null || name is null) continue;
                await using var cmd = ds.CreateCommand("""
                    INSERT INTO zentriq.app_control_events (asset_id, agent_id, occurred_at, kind, name, publisher, version, sha256, path, user_name, platform, detail)
                    VALUES (@a, @ag, @at, @k, @n, @p, @v, @h, @f, @u, @pf, @d)
                    """);
                cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)agent.AssetId ?? DBNull.Value });
                cmd.Parameters.AddWithValue("ag", agent.Id);
                cmd.Parameters.AddWithValue("at", e.At is { } at && at < DateTimeOffset.UtcNow.AddMinutes(10) && at > DateTimeOffset.UtcNow.AddDays(-30) ? at : DateTimeOffset.UtcNow);
                cmd.Parameters.AddWithValue("k", kind);
                cmd.Parameters.AddWithValue("n", name);
                cmd.Parameters.Add(Text("p", Trunc(e.Publisher, 300)));
                cmd.Parameters.Add(Text("v", Trunc(e.Version, 100)));
                cmd.Parameters.Add(Text("h", AppControl.NormalizeSha256(e.Sha256)));
                cmd.Parameters.Add(Text("f", Trunc(e.Path, 1000)));
                cmd.Parameters.Add(Text("u", Trunc(e.User, 200)));
                cmd.Parameters.AddWithValue("pf", platform);
                cmd.Parameters.Add(Text("d", Trunc(e.Detail, 2000)));
                stored += await cmd.ExecuteNonQueryAsync(ct);
            }
            return Results.Ok(new { stored });
        }).AllowAnonymous();

        g.MapPost("/app-control/requests", async (AppRequestBody body, HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            var name = Trunc(body.Name, 300);
            if (name is null) return Results.Problem(statusCode: 400, title: "The software name is required.");
            var settings = await AppControlStore.LoadSettingsAsync(ds, ct);
            if (!settings.AllowRequests) return Results.Problem(statusCode: 403, title: "Requests are turned off. Contact your administrator.");
            var platform = AppControlStore.Family(await PlatformOfAsync(ds, agent.Id, ct));
            // One open request per device and software (asking again updates the reason).
            await using var cmd = ds.CreateCommand("""
                WITH open AS (
                    UPDATE zentriq.app_control_requests SET reason = COALESCE(@r, reason), user_name = COALESCE(@u, user_name), created_at = now()
                     WHERE agent_id = @ag AND status = 'pending' AND lower(name) = lower(@n) AND COALESCE(version, '') = COALESCE(@v, '')
                    RETURNING id)
                INSERT INTO zentriq.app_control_requests (asset_id, agent_id, user_name, name, publisher, version, sha256, path, platform, reason)
                SELECT @a, @ag, @u, @n, @p, @v, @h, @f, @pf, @r WHERE NOT EXISTS (SELECT 1 FROM open)
                RETURNING id
                """);
            cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)agent.AssetId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("ag", agent.Id);
            cmd.Parameters.Add(Text("u", Trunc(body.User, 200)));
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.Add(Text("p", Trunc(body.Publisher, 300)));
            cmd.Parameters.Add(Text("v", Trunc(body.Version, 100)));
            cmd.Parameters.Add(Text("h", AppControl.NormalizeSha256(body.Sha256)));
            cmd.Parameters.Add(Text("f", Trunc(body.Path, 1000)));
            cmd.Parameters.AddWithValue("pf", platform);
            cmd.Parameters.Add(Text("r", Trunc(body.Reason, 1000)));
            var id = await cmd.ExecuteScalarAsync(ct);
            return Results.Ok(new { id, message = "Your request was sent to your administrator." });
        }).AllowAnonymous();

        // A package the device was asked to install (only while such an action is open for this agent).
        g.MapGet("/deploy/{packageId:guid}", async (Guid packageId, HttpContext http, NpgsqlDataSource ds, AgentServer server, ZentriqPaths paths, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            await using var cmd = ds.CreateCommand("""
                SELECT p.file_name, p.sha256 FROM zentriq.deployment_packages p
                 WHERE p.id = @p AND EXISTS (SELECT 1 FROM zentriq.asset_actions x WHERE x.agent_id = @ag AND x.kind = 'deploy' AND x.target = @t
                                               AND x.status IN ('queued','sent','running'))
                """);
            cmd.Parameters.AddWithValue("p", packageId);
            cmd.Parameters.AddWithValue("ag", agent.Id);
            cmd.Parameters.AddWithValue("t", packageId.ToString());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            var path = DeploymentFiles.PathOf(paths, packageId);
            if (!File.Exists(path)) return Results.Problem(statusCode: 410, title: "The package file is missing on the server. Upload the package again.");
            return Results.File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true), "application/octet-stream", r.GetString(0), enableRangeProcessing: true);
        }).AllowAnonymous();
    }

    private static async Task<string> PlatformOfAsync(NpgsqlDataSource ds, Guid agentId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT platform FROM zentriq.agent_registrations WHERE id = @id");
        cmd.Parameters.AddWithValue("id", agentId);
        return await cmd.ExecuteScalarAsync(ct) as string ?? AgentPackages.Windows;
    }
}

/// <summary>Where uploaded deployment packages are kept: &lt;data&gt;\Deployment\&lt;package id&gt;.bin.</summary>
public static class DeploymentFiles
{
    public static string PathOf(ZentriqPaths paths, Guid id) => Path.Combine(paths.DeploymentPackages, id.ToString("N") + ".bin");
}
