using Npgsql;
using Zentriq.Api.Infrastructure;
using Zentriq.Infrastructure.Agents;
using Zentriq.Infrastructure.Audit;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Deployment;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// The tray icon on Windows devices, through the agent (client certificate required, like every agent call). The agent
/// service asks on behalf of the signed-in user of the session whose tray asked:
///   GET  /api/agent/v1/tray/config           what the tray shows
///   GET  /api/agent/v1/tray/catalog          Software Center: the software offered to this device and its state here
///   POST /api/agent/v1/tray/install          install one (or ask IT when it needs approval)
///   POST /api/agent/v1/tray/help             Get Remote Help: a help request for the technicians
///   GET  /api/agent/v1/tray/help             the user's recent help requests on this device
/// </summary>
public static partial class AgentEndpoints
{
    public sealed record TrayInstallBody(Guid PackageId, string? User, string? Reason);
    public sealed record TrayHelpBody(string? User, string? Message, string? Contact);

    private static void MapAgentTrayEndpoints(RouteGroupBuilder g)
    {
        g.MapGet("/tray/config", async (HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            return Results.Ok(await SelfService.SettingsAsync(ds, ct));
        }).AllowAnonymous();

        g.MapGet("/tray/catalog", async (HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            if (agent.AssetId is not { } assetId) return Results.Ok(Array.Empty<CatalogItem>());
            var s = await SelfService.SettingsAsync(ds, ct);
            if (!s.Enabled || !s.SoftwareCenter) return Results.Ok(Array.Empty<CatalogItem>());
            return Results.Ok(await SelfService.CatalogAsync(ds, assetId, AppControlStore.Family(await PlatformOfAsync(ds, agent.Id, ct)), ct));
        }).AllowAnonymous();

        g.MapPost("/tray/install", async (TrayInstallBody body, HttpContext http, NpgsqlDataSource ds, AgentServer server, ZentriqPaths paths, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            if (agent.AssetId is not { } assetId) return Results.Problem(statusCode: 409, title: "This agent has not sent its inventory yet.");
            var s = await SelfService.SettingsAsync(ds, ct);
            if (!s.Enabled || !s.SoftwareCenter) return Results.Problem(statusCode: 403, title: "The Software Center is turned off.");
            var res = await SelfService.RequestAsync(ds, body.PackageId, assetId, agent.Id, AppControlStore.Family(await PlatformOfAsync(ds, agent.Id, ct)), body.User, body.Reason,
                id => File.Exists(DeploymentFiles.PathOf(paths, id)), ct);
            if (res.State == "installing") AgentPresence.Instance.Nudge();
            await AuditAgentAsync(http, agent, "selfservice." + (res.State == "pending" ? "request" : res.State == "installing" ? "install" : "refused"), body.PackageId.ToString(),
                new { user = body.User, res.State, res.Message }, ct);
            return Results.Ok(res);
        }).AllowAnonymous();

        g.MapPost("/tray/help", async (TrayHelpBody body, HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            if (agent.AssetId is not { } assetId) return Results.Problem(statusCode: 409, title: "This agent has not sent its inventory yet.");
            var s = await SelfService.SettingsAsync(ds, ct);
            if (!s.Enabled || !s.RemoteHelp) return Results.Problem(statusCode: 403, title: "Get Remote Help is turned off.");
            if (string.IsNullOrWhiteSpace(body.Message)) return Validation.Problem("message", "Describe the problem.");
            var number = await SelfService.CreateHelpAsync(ds, assetId, body.User, body.Message, body.Contact, ct);
            await AuditAgentAsync(http, agent, "help.request", $"#{number}", new { user = body.User }, ct);
            return Results.Ok(new { number, message = $"Help request #{number} was sent. A technician will contact you." });
        }).AllowAnonymous();

        g.MapGet("/tray/help", async (string? user, HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            if (agent.AssetId is not { } assetId) return Results.Ok(Array.Empty<object>());
            await using var cmd = ds.CreateCommand("""
                SELECT number, message, status, created_at, updated_at FROM zentriq.help_requests
                 WHERE asset_id = @a AND (@u = '' OR lower(user_name) = lower(@u)) ORDER BY created_at DESC LIMIT 10
                """);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.AddWithValue("u", user ?? "");
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new { number = r.GetInt64(0), message = r.GetString(1), status = r.GetString(2), createdAt = r.GetFieldValue<DateTimeOffset>(3), updatedAt = r.GetFieldValue<DateTimeOffset>(4) });
            return Results.Ok(list);
        }).AllowAnonymous();
    }

    /// <summary>Audit entry for something the device asked on behalf of its user (no console user).</summary>
    private static Task AuditAgentAsync(HttpContext http, AgentIdentity agent, string action, string? target, object detail, CancellationToken ct) =>
        http.RequestServices.GetRequiredService<AuditWriter>().WriteAsync(new AuditActor(null, $"agent:{agent.Id}", http.Connection.RemoteIpAddress?.ToString()), action, target, detail: detail, ct: ct);
}
