using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Jobs;

namespace Zentriq.Api.Endpoints;

public static class SystemEndpoints
{
    public sealed record SettingBody(JsonElement Value);

    /// <summary>Settings an administrator may change through the API (others are installer-owned).</summary>
    public static readonly string[] EditableSettings =
    [
        "discovery.approved_scopes", "discovery.max_targets", "discovery.confirm_above", "discovery.min_prefix",
        "discovery.default_concurrency", "discovery.max_concurrency", "discovery.probe_timeout_ms",
        "security.mfa_required", "security.lockout_threshold", "security.lockout_minutes",
    ];

    public static void MapSystemEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/system").WithTags("System & maintenance");

        g.MapGet("/info", async (ZentriqDbContext db, NpgsqlDataSource ds, SqlMigrator migrator, ZentriqOptions options, ZentriqPaths paths, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var schema = await migrator.GetStatusAsync(conn, ct);
            var state = await db.InstallState.AsNoTracking().FirstOrDefaultAsync(ct);
            var workers = await db.WorkerHeartbeats.AsNoTracking().OrderByDescending(w => w.LastBeatAt).ToListAsync(ct);
            var history = await db.InstallationHistory.AsNoTracking().OrderByDescending(h => h.Id).Take(20)
                .Select(h => new { h.OccurredAt, h.Action, h.FromVersion, h.ToVersion, h.Outcome }).ToListAsync(ct);
            await using var v = new NpgsqlCommand("SHOW server_version", conn);
            var pgVersion = (string?)await v.ExecuteScalarAsync(ct);
            return Results.Ok(new
            {
                productVersion = options.ProductVersion, installationId = state?.InstallationId, initializedAt = state?.InitializedAt,
                schemaVersion = schema.CurrentVersion, expectedSchemaVersion = schema.LatestVersion, postgresVersion = pgVersion,
                database = new { options.Database.Host, options.Database.Port, options.Database.Database },
                https = new { options.Https.Port, options.Https.HostName },
                paths = new { paths.Root, paths.Logs, paths.Backups },
                clockRollbackDetectedAt = state?.ClockRollbackDetectedAt,
                workers = workers.Select(w => new { w.WorkerId, w.HostName, w.Version, w.StartedAt, w.LastBeatAt, healthy = w.LastBeatAt > DateTimeOffset.UtcNow.AddMinutes(-2) }),
                history,
            });
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapGet("/settings", async (ZentriqDbContext db, CancellationToken ct) =>
        {
            var rows = await db.SystemSettings.AsNoTracking().Where(s => EditableSettings.Contains(s.Key)).ToListAsync(ct);
            return Results.Ok(rows.ToDictionary(r => r.Key, r => JsonDocument.Parse(r.Value).RootElement));
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapPut("/settings/{key}", async (string key, SettingBody body, ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            if (!EditableSettings.Contains(key)) return Results.Problem(statusCode: 404, title: "Unknown or read-only setting.");
            var err = ValidateSetting(key, body.Value);
            if (err is not null) return Validation.Problem("value", err);
            var s = await db.SystemSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
            var old = s?.Value;
            if (s is null) db.SystemSettings.Add(s = new SystemSetting { Key = key });
            s.Value = body.Value.GetRawText(); s.UpdatedAt = DateTimeOffset.UtcNow; s.UpdatedBy = rc.UserId;
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("settings.update", key, detail: new { from = old, to = s.Value });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SettingsManage);

        // Backups run in the privileged maintenance task (zentriqctl, SYSTEM), not in the web process.
        g.MapPost("/backups", async (JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            var id = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.MaintenanceBackup, new { requestedBy = rc.UserName }, $"backup:{DateTimeOffset.UtcNow:yyyyMMddHHmm}",
                Priority: 10, MaxAttempts: 1, TimeoutSeconds: 4 * 3600, CreatedBy: rc.UserId), ct: ct);
            await rc.AuditAsync("backup.request", id.ToString());
            return Results.Accepted($"/api/v1/jobs/{id}", new { jobId = id, note = "The backup runs within 5 minutes via the Zentriq maintenance task." });
        }).RequireAuthorization(Permissions.BackupRun);

        g.MapGet("/backups", (ZentriqPaths paths) =>
        {
            if (!Directory.Exists(paths.Backups)) return Results.Ok(Array.Empty<object>());
            try
            {
                var files = new DirectoryInfo(paths.Backups).EnumerateFiles("*.zbak").OrderByDescending(f => f.CreationTimeUtc).Take(100)
                    .Select(f => new { name = f.Name, sizeBytes = f.Length, createdAt = new DateTimeOffset(f.CreationTimeUtc, TimeSpan.Zero) });
                return Results.Ok(files.ToList());
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Problem(statusCode: 403, title: "The API service cannot list the backup folder. Use 'zentriqctl backup list' on the server.");
            }
        }).RequireAuthorization(Permissions.BackupRun);

        g.MapGet("/notifications", async (ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var uid = rc.UserId;
            var isAdmin = rc.Has(Permissions.SettingsManage);
            var rows = await db.Notifications.AsNoTracking().Where(n => n.UserId == uid || (n.UserId == null && isAdmin))
                .OrderByDescending(n => n.Id).Take(50).ToListAsync(ct);
            return Results.Ok(rows.Select(n => new { n.Id, n.Kind, n.Title, n.Body, n.Link, n.CreatedAt, read = n.ReadAt != null }));
        }).RequireAuthorization();

        g.MapPost("/notifications/read", async (ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var uid = rc.UserId;
            var isAdmin = rc.Has(Permissions.SettingsManage);
            await db.Notifications.Where(n => n.ReadAt == null && (n.UserId == uid || (n.UserId == null && isAdmin)))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static string? ValidateSetting(string key, JsonElement v)
    {
        if (key == "discovery.approved_scopes")
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() == 0) return "Provide at least one CIDR network.";
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind != JsonValueKind.String || !Zentriq.Application.Discovery.TargetParser.TryParseCidr(e.GetString()!, 8, out _, out var err))
                    return $"Invalid scope: {e}. Scopes must be CIDR networks no broader than /8.";
            return null;
        }
        if (key == "security.mfa_required") return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Must be true or false.";
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n)) return "Must be a whole number.";
        return key switch
        {
            "discovery.max_targets" => n is >= 1 and <= 1_048_576 ? null : "Must be between 1 and 1,048,576.",
            "discovery.min_prefix" => n is >= 8 and <= 32 ? null : "Must be between 8 and 32.",
            "discovery.max_concurrency" or "discovery.default_concurrency" => n is >= 1 and <= 512 ? null : "Must be between 1 and 512.",
            "discovery.probe_timeout_ms" => n is >= 200 and <= 10000 ? null : "Must be between 200 and 10000.",
            "security.lockout_threshold" => n is >= 3 and <= 20 ? null : "Must be between 3 and 20.",
            "security.lockout_minutes" => n is >= 1 and <= 1440 ? null : "Must be between 1 and 1440.",
            _ => n >= 0 ? null : "Must be zero or more.",
        };
    }
}
