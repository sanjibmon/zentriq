using Npgsql;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Backup;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Jobs;
using Zentriq.Infrastructure.Logs;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Retention and export of the audit log and the activity log: how long entries are kept (one year by default), archives
/// made on request or every 3, 6 or 12 months (zip with CSV files and a manifest), copies to a backup destination, and a
/// direct CSV download of a date range. Archives are written by the maintenance task, which alone may remove audit entries.
/// </summary>
public static class LogEndpoints
{
    public sealed record SettingsBody(int? AuditRetentionDays, int? ActivityRetentionDays, bool? ArchiveBeforeDelete, LogExportSchedule? Schedule, Guid? DestinationId, bool? ClearDestination);
    public sealed record ArchiveBody(DateTimeOffset? From, DateTimeOffset? To, bool? IncludeAudit, bool? IncludeActivity);

    public static void MapLogEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/logs").WithTags("Log retention").RequireAuthorization(Permissions.AuditView);

        g.MapGet("", async (NpgsqlDataSource ds, BackupStore backups, RequestContext rc, CancellationToken ct) =>
        {
            var s = await new LogStore(ds).LoadAsync(ct);
            var now = DateTimeOffset.UtcNow;
            async Task<object> Stats(string sql)
            {
                await using var cmd = ds.CreateCommand(sql);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                return new { rows = r.GetInt64(0), oldest = r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1) };
            }
            var next = s.Schedule.Enabled ? NextScheduled(s.Schedule, now) : (DateTimeOffset?)null;
            return Results.Ok(new
            {
                settings = s,
                scheduleText = s.Schedule.Describe(),
                nextScheduled = next,
                lastPeriod = s.Schedule.Enabled ? (object)new { from = s.Schedule.LastPeriod(now).From, to = s.Schedule.LastPeriod(now).To } : null,
                auditCutoff = LogSettings.Cutoff(s.AuditRetentionDays, now),
                activityCutoff = LogSettings.Cutoff(s.ActivityRetentionDays, now),
                audit = await Stats("SELECT count(*), min(occurred_at) FROM zentriq.audit_events"),
                activity = await Stats("SELECT count(*), min(requested_at) FROM zentriq.asset_actions"),
                retentionChoices = LogSettings.RetentionChoices,
                destinations = rc.Has(Permissions.SettingsManage) || rc.Has(Permissions.BackupRun)
                    ? (await backups.DestinationsAsync(false, ct)).Select(d => (object)new { id = d.Destination.Id, name = d.Destination.Name, kind = d.Destination.Kind, enabled = d.Destination.Enabled }).ToList()
                    : new List<object>(),
            });
        });

        g.MapPut("/settings", async (SettingsBody body, NpgsqlDataSource ds, BackupStore backups, RequestContext rc, CancellationToken ct) =>
        {
            var store = new LogStore(ds);
            var s = await store.LoadAsync(ct);
            var before = s.ToJson();
            if (body.AuditRetentionDays is { } a) s.AuditRetentionDays = a;
            if (body.ActivityRetentionDays is { } x) s.ActivityRetentionDays = x;
            if (body.ArchiveBeforeDelete is { } abd) s.ArchiveBeforeDelete = abd;
            if (body.Schedule is { } sch) s.Schedule = sch;
            if (body.ClearDestination == true) s.DestinationId = null;
            else if (body.DestinationId is { } dest)
            {
                if (await backups.DestinationAsync(dest, false, ct) is null) return Validation.Problem("destinationId", "Choose an existing backup destination.");
                s.DestinationId = dest;
            }
            if (s.Validate() is { } problem) return Validation.Problem("settings", problem);
            await store.SaveAsync(s, rc.UserId, ct);
            await rc.AuditAsync("logs.settings", "log retention", detail: new
            {
                s.AuditRetentionDays, s.ActivityRetentionDays, s.ArchiveBeforeDelete, schedule = s.Schedule.Describe(), s.DestinationId,
                previous = System.Text.Json.JsonDocument.Parse(before).RootElement,
            });
            return Results.Ok(s);
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapGet("/archives", async (int? page, int? pageSize, NpgsqlDataSource ds, ZentriqPaths paths, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            long total;
            await using (var c = ds.CreateCommand("SELECT count(*) FROM zentriq.log_archives")) total = (long)(await c.ExecuteScalarAsync(ct))!;
            await using var cmd = ds.CreateCommand("""
                SELECT id, trigger, period_from, period_to, include_audit, include_activity, file_name, size_bytes, sha256, audit_rows, activity_rows,
                       status, copy::text, error, requested_by, created_at, finished_at
                  FROM zentriq.log_archives ORDER BY created_at DESC LIMIT @l OFFSET @o
                """);
            cmd.Parameters.AddWithValue("l", ps);
            cmd.Parameters.AddWithValue("o", (p - 1) * ps);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
            while (await r.ReadAsync(ct))
            {
                var file = S(6);
                items.Add(new
                {
                    id = r.GetGuid(0), trigger = r.GetString(1), from = r.GetFieldValue<DateTimeOffset>(2), to = r.GetFieldValue<DateTimeOffset>(3),
                    includeAudit = r.GetBoolean(4), includeActivity = r.GetBoolean(5), fileName = file, size = L(7), sha256 = S(8), auditRows = L(9), activityRows = L(10),
                    status = r.GetString(11), copy = S(12) is { } cj ? System.Text.Json.JsonDocument.Parse(cj).RootElement : (System.Text.Json.JsonElement?)null,
                    error = S(13), requestedBy = S(14), createdAt = r.GetFieldValue<DateTimeOffset>(15),
                    finishedAt = r.IsDBNull(16) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(16),
                    available = file is not null && File.Exists(Path.Combine(paths.LogArchives, file)),
                });
            }
            return Results.Ok(new { items, total, page = p, pageSize = ps });
        });

        // Export a period now (runs in the maintenance task within a few minutes).
        g.MapPost("/archives", async (ArchiveBody body, NpgsqlDataSource ds, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            var audit = body.IncludeAudit ?? true;
            var activity = body.IncludeActivity ?? true;
            if (!audit && !activity) return Validation.Problem("includeAudit", "Choose the audit log, the activity log or both.");
            if (activity && !rc.Has(Permissions.ActivityView)) return Results.Problem(statusCode: 403, title: "You cannot export the activity log.");
            if (body.From is not { } from || body.To is not { } to) return Validation.Problem("from", "Choose the period to export.");
            if (to <= from) return Validation.Problem("to", "The end must be after the start.");
            if (to - from > TimeSpan.FromDays(3660)) return Validation.Problem("to", "Export at most ten years at a time.");
            await using (var busy = ds.CreateCommand("SELECT count(*) FROM zentriq.log_archives WHERE status IN ('queued', 'running') AND trigger = 'manual'"))
                if ((long)(await busy.ExecuteScalarAsync(ct))! >= 3) return Results.Problem(statusCode: 409, title: "Three exports are already waiting. Try again when they have finished.");
            var store = new LogStore(ds);
            var id = await store.AddAsync("manual", from, to, audit, activity, "queued", rc.UserName, ct);
            var job = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.MaintenanceLogArchive, new { archiveId = id, requestedBy = rc.UserName }, $"logs:{id}",
                Priority: 20, MaxAttempts: 1, TimeoutSeconds: 4 * 3600, CreatedBy: rc.UserId), ct: ct);
            await rc.AuditAsync("logs.export_request", $"{from:yyyy-MM-dd} – {to:yyyy-MM-dd}", detail: new { archiveId = id, jobId = job, audit, activity });
            return Results.Accepted($"/api/v1/jobs/{job}", new { id, jobId = job, note = "The export starts within 5 minutes (Zentriq maintenance task). It appears in the history below with a download link." });
        });

        g.MapGet("/archives/{id:guid}/file", async (Guid id, NpgsqlDataSource ds, ZentriqPaths paths, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT file_name, include_activity FROM zentriq.log_archives WHERE id = @id AND status = 'succeeded'");
            cmd.Parameters.AddWithValue("id", id);
            string? name; bool activity;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct) || r.IsDBNull(0)) return Results.NotFound();
                name = r.GetString(0); activity = r.GetBoolean(1);
            }
            if (activity && !rc.Has(Permissions.ActivityView)) return Results.Problem(statusCode: 403, title: "This archive contains the activity log, which you cannot see.");
            var file = Path.GetFileName(name);
            var path = Path.Combine(paths.LogArchives, file);
            if (!File.Exists(path)) return Results.Problem(statusCode: 410, title: "The archive file is no longer on the server.");
            await rc.AuditAsync("logs.download", file);
            return Results.File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true), "application/zip", file, enableRangeProcessing: true);
        });

        g.MapDelete("/archives/{id:guid}", async (Guid id, NpgsqlDataSource ds, ZentriqPaths paths, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.log_archives WHERE id = @id AND status NOT IN ('queued', 'running') RETURNING file_name");
            cmd.Parameters.AddWithValue("id", id);
            var name = await cmd.ExecuteScalarAsync(ct);
            if (name is null) return Results.NotFound();
            if (name is string n)
            {
                try { File.Delete(Path.Combine(paths.LogArchives, Path.GetFileName(n))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the history row is gone; the file stays */ }
            }
            await rc.AuditAsync("logs.archive_delete", name as string ?? id.ToString());
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SettingsManage);

        // Direct CSV downloads of a date range (what the Audit Logs and Activity Log pages show, without paging).
        api.MapGet("/audit/export", (DateTimeOffset? from, DateTimeOffset? to, HttpContext http, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
            Csv(http, ds, rc, "audit", LogArchiver.AuditSql, LogArchiver.AuditHeader, from, to, ct))
            .WithTags("Audit").RequireAuthorization(Permissions.AuditView);
        api.MapGet("/activity/export", (DateTimeOffset? from, DateTimeOffset? to, HttpContext http, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
            Csv(http, ds, rc, "activity", LogArchiver.ActivitySql, LogArchiver.ActivityHeader, from, to, ct))
            .WithTags("Assets").RequireModule(Modules.Inventory).RequireAuthorization(Permissions.ActivityView);
    }

    private static async Task<IResult> Csv(HttpContext http, NpgsqlDataSource ds, RequestContext rc, string which, string sql, string header,
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var f = from ?? DateTimeOffset.UnixEpoch;
        var t = to ?? DateTimeOffset.UtcNow.AddMinutes(1);
        if (t <= f) return Validation.Problem("to", "The end must be after the start.");
        await rc.AuditAsync($"{which}.export", $"{(from is null ? "start" : f.ToString("yyyy-MM-dd"))} – {t:yyyy-MM-dd}");
        var name = $"zentriq-{which}-{(from is null ? "all" : f.ToString("yyyyMMdd"))}-{t:yyyyMMdd}.csv";
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
        http.Response.Headers.CacheControl = "no-store";
        await LogArchiver.WriteCsvAsync(ds, sql, header, f, t, http.Response.Body, ct);
        return Results.Empty;
    }

    /// <summary>When the next scheduled export runs: the end of the current period (the maintenance task picks it up within minutes).</summary>
    public static DateTimeOffset NextScheduled(LogExportSchedule s, DateTimeOffset now)
    {
        var (_, end) = s.LastPeriod(now);
        var zone = TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(end, zone).DateTime;
        return LogSettings.At(local.AddMonths(s.EveryMonths), zone);
    }
}
