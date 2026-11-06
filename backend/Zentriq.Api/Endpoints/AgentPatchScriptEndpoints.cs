using Npgsql;
using Zentriq.Infrastructure.Agents;
using Zentriq.Infrastructure.Deployment;
using Zentriq.Infrastructure.Patches;
using Zentriq.Infrastructure.Scripts;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Patch management and scripts on the agent port (client certificate required, like every agent call):
///   POST /api/agent/v1/patches                  the result of a scan for missing updates (and of an installation)
///   POST /api/agent/v1/actions/{id}/output      the output and exit code of a script the device ran
/// </summary>
public static partial class AgentEndpoints
{
    public sealed record ScriptOutputBody(int? ExitCode, string? Stdout, string? Stderr, string? Outcome, int? DurationMs);

    private static void MapAgentPatchScriptEndpoints(RouteGroupBuilder g)
    {
        g.MapPost("/patches", async (ScanReport body, HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            if (agent.AssetId is not { } assetId) return Results.Problem(statusCode: 409, title: "This agent has not sent its inventory yet.");
            var platform = AppControlStore.Family(await PlatformOfAsync(ds, agent.Id, ct));
            var res = await PatchStore.IngestAsync(ds, assetId, platform, body, ct);
            return Results.Ok(new { missing = res.Missing, installed = res.Installed, failed = res.Failed });
        }).AllowAnonymous();

        g.MapPost("/actions/{actionId:guid}/output", async (Guid actionId, ScriptOutputBody body, HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            var ok = await ScriptStore.SaveOutputAsync(ds, actionId, agent.Id, body.ExitCode, body.Stdout, body.Stderr, body.Outcome, body.DurationMs, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }).AllowAnonymous();
    }
}
