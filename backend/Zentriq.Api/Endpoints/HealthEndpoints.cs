using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Database;

namespace Zentriq.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: the process is up.
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();

        // Readiness: database reachable with the app role, schema at the version this build expects.
        // Used by the installer, load balancers and monitoring. Reveals no sensitive detail.
        app.MapGet("/health/ready", async (NpgsqlDataSource ds, SqlMigrator migrator, ZentriqDbContext db, CancellationToken ct) =>
        {
            var checks = new Dictionary<string, object?>();
            var ok = true;
            try
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                checks["database"] = "ok";
                var status = await migrator.GetStatusAsync(conn, ct);
                checks["schemaVersion"] = status.CurrentVersion;
                checks["expectedSchemaVersion"] = status.LatestVersion;
                if (!status.UpToDate) { ok = false; checks["schema"] = "pending migrations"; }
                var initialized = await db.InstallState.AsNoTracking().Select(s => s.InitializedAt != null).FirstOrDefaultAsync(ct);
                checks["initialized"] = initialized;
                var beat = await db.WorkerHeartbeats.AsNoTracking().MaxAsync(h => (DateTimeOffset?)h.LastBeatAt, ct);
                checks["worker"] = beat is not null && beat > DateTimeOffset.UtcNow.AddMinutes(-2) ? "ok" : "no recent heartbeat";
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            {
                ok = false;
                checks["database"] = "unavailable";
            }
            return Results.Json(new { status = ok ? "ready" : "not-ready", checks }, statusCode: ok ? 200 : 503);
        }).AllowAnonymous();
    }
}
