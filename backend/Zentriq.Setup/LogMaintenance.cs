using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Backup;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Logs;

namespace Zentriq.Setup;

/// <summary>
/// Log retention and export, run by the maintenance task with the database owner connection:
///   * archives requested in the console (maintenance.log_archive jobs),
///   * scheduled archives of the quarter, half-year or year that just ended,
///   * removal of audit and activity entries older than their retention, archived first when chosen.
/// Archives go to Exports\LogArchives and, when a destination is chosen, are copied there as well.
/// </summary>
public sealed class LogMaintenance(ZentriqPaths paths, NpgsqlDataSource ds, BackupStore backups, Reporter log)
{
    private readonly LogStore store = new(ds);

    /// <summary>Writes one archive, records it and copies it to the chosen destination. Returns null when it failed.</summary>
    public async Task<LogArchiveResult?> ArchiveAsync(Guid id, LogSettings settings, DateTimeOffset from, DateTimeOffset to, bool audit, bool activity, CancellationToken ct)
    {
        await using (var run = ds.CreateCommand("UPDATE zentriq.log_archives SET status = 'running' WHERE id = @id"))
        {
            run.Parameters.AddWithValue("id", id);
            await run.ExecuteNonQueryAsync(ct);
        }
        try
        {
            var r = await LogArchiver.WriteAsync(ds, paths.LogArchives, from, to, audit, activity, Environment.MachineName, ct);
            log.Info($"Log archive {Path.GetFileName(r.File)}: {r.AuditRows} audit and {r.ActivityRows} activity entries ({r.Size / 1024} KiB).");
            object? copy = null;
            if (settings.DestinationId is { } dest)
            {
                var row = await backups.DestinationAsync(dest, true, ct);
                if (row is null) copy = new { status = "failed", message = "The chosen destination no longer exists." };
                else
                {
                    try
                    {
                        using var target = BackupTargets.Create(row.Destination);
                        await BackupTargets.RetryAsync(async () => { await target.UploadAsync(r.File, Path.GetFileName(r.File), ct); return true; }, ct);
                        copy = new { status = "ok", name = row.Destination.Name, message = $"Copied to {target.Where}" };
                        log.Info($"{row.Destination.Name}: log archive copied to {target.Where}.");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        copy = new { status = "failed", name = row.Destination.Name, message = ex.Message };
                        log.Warn($"{row.Destination.Name}: log archive copy failed: {ex.Message}");
                        await NotifyAsync("warn", "Log archive copy failed", $"{Path.GetFileName(r.File)} was saved on the server, but copying it to {row.Destination.Name} failed: {ex.Message}");
                    }
                }
            }
            await store.FinishAsync(id, r, copy, null, ct);
            return r;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error("Log archive failed: " + ex.Message);
            await store.FinishAsync(id, null, null, ex.Message, CancellationToken.None);
            await NotifyAsync("crit", "Log archive failed", $"The log archive for {from:yyyy-MM-dd} – {to.AddSeconds(-1):yyyy-MM-dd} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The scheduled archive and the retention clean-up; called on every maintenance run (cheap when nothing is due).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var settings = await store.LoadAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // Scheduled export of the last completed period.
        if (settings.Schedule.Enabled)
        {
            var (from, to) = settings.Schedule.LastPeriod(now);
            if (!await store.HasScheduledAsync(from, ct) && !await RecentFailureAsync(from, ct))
            {
                log.Info($"Scheduled log export {from:yyyy-MM-dd} – {to.AddSeconds(-1):yyyy-MM-dd} ({settings.Schedule.Describe()}).");
                var id = await store.AddAsync("schedule", from, to, settings.Schedule.IncludeAudit, settings.Schedule.IncludeActivity, "queued", "Schedule", ct);
                await ArchiveAsync(id, settings, from, to, settings.Schedule.IncludeAudit, settings.Schedule.IncludeActivity, ct);
            }
        }

        // Retention: the audit log (through the owner-only purge function) and the activity log.
        if (LogSettings.Cutoff(settings.AuditRetentionDays, now) is { } auditCut)
            await PurgeAsync("audit", auditCut, settings, ct);
        if (LogSettings.Cutoff(settings.ActivityRetentionDays, now) is { } activityCut)
            await PurgeAsync("activity", activityCut, settings, ct);
    }

    private async Task PurgeAsync(string which, DateTimeOffset cutoff, LogSettings settings, CancellationToken ct)
    {
        var audit = which == "audit";
        await using (var oldest = ds.CreateCommand(audit
            ? "SELECT min(occurred_at) FROM zentriq.audit_events WHERE occurred_at < @c AND action <> 'audit.purged'"
            : "SELECT min(requested_at) FROM zentriq.asset_actions WHERE requested_at < @c AND status NOT IN ('queued', 'sent', 'running')"))
        {
            oldest.Parameters.AddWithValue("c", cutoff.ToUniversalTime());
            if (await oldest.ExecuteScalarAsync(ct) is not DateTime first) return;
            if (settings.ArchiveBeforeDelete)
            {
                var from = new DateTimeOffset(DateTime.SpecifyKind(first, DateTimeKind.Utc)).AddSeconds(-1);
                var id = await store.AddAsync("before-delete", from, cutoff, audit, !audit, "queued", "Retention", ct);
                if (await ArchiveAsync(id, settings, from, cutoff, audit, !audit, ct) is null)
                {
                    log.Warn($"The {which} log was not trimmed because the archive before deletion failed.");
                    return;
                }
            }
        }
        if (audit)
        {
            await using var cmd = ds.CreateCommand("SELECT zentriq.purge_audit_events(@c)");
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.TimestampTz) { Value = cutoff.ToUniversalTime() });
            var n = await cmd.ExecuteScalarAsync(ct);
            log.Info($"Audit log: {n} entries before {cutoff:yyyy-MM-dd} removed (retention {settings.AuditRetentionDays} days).");
        }
        else
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.asset_actions WHERE requested_at < @c AND status NOT IN ('queued', 'sent', 'running')");
            cmd.Parameters.AddWithValue("c", cutoff.ToUniversalTime());
            var n = await cmd.ExecuteNonQueryAsync(ct);
            log.Info($"Activity log: {n} entries before {cutoff:yyyy-MM-dd} removed (retention {settings.ActivityRetentionDays} days).");
            await new Zentriq.Infrastructure.Audit.AuditWriter(ds).WriteAsync(new Zentriq.Infrastructure.Audit.AuditActor(null, "Zentriq maintenance", null),
                "activity.purged", "activity log", "Success", new { before = cutoff, rows = n, retentionDays = settings.ActivityRetentionDays }, ct: ct);
        }
    }

    /// <summary>A failed scheduled archive is retried after six hours, not on every run.</summary>
    private async Task<bool> RecentFailureAsync(DateTimeOffset from, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT EXISTS (SELECT 1 FROM zentriq.log_archives WHERE trigger = 'schedule' AND period_from = @f AND status = 'failed' AND created_at > now() - interval '6 hours')");
        cmd.Parameters.AddWithValue("f", from.ToUniversalTime());
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>A console request (maintenance.log_archive job; the row was added by the console).</summary>
    public async Task<object> JobAsync(string payload, CancellationToken ct)
    {
        using var d = JsonDocument.Parse(payload);
        var id = d.RootElement.GetProperty("archiveId").GetGuid();
        await using var q = ds.CreateCommand("SELECT period_from, period_to, include_audit, include_activity FROM zentriq.log_archives WHERE id = @id");
        q.Parameters.AddWithValue("id", id);
        DateTimeOffset from, to; bool audit, activity;
        await using (var r = await q.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) throw new SetupException("The requested log archive no longer exists.");
            from = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc));
            to = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc));
            audit = r.GetBoolean(2); activity = r.GetBoolean(3);
        }
        var settings = await store.LoadAsync(ct);
        var result = await ArchiveAsync(id, settings, from, to, audit, activity, ct) ?? throw new SetupException("The log archive failed; see the archive history.");
        return new { file = Path.GetFileName(result.File), bytes = result.Size, auditRows = result.AuditRows, activityRows = result.ActivityRows };
    }

    private async Task NotifyAsync(string kind, string title, string body)
    {
        try
        {
            await using var cmd = ds.CreateCommand("INSERT INTO zentriq.notifications (user_id, kind, title, body, link) VALUES (NULL, @k, @t, @b, '/audit?tab=retention')");
            cmd.Parameters.AddWithValue("k", kind);
            cmd.Parameters.AddWithValue("t", title);
            cmd.Parameters.AddWithValue("b", body.Length > 1000 ? body[..1000] : body);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is NpgsqlException or PostgresException) { log.Warn("Could not add a console notification: " + ex.Message); }
    }
}
