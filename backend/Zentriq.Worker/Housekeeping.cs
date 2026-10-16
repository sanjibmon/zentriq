using Npgsql;
using Zentriq.Domain;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Jobs;

namespace Zentriq.Worker;

/// <summary>
/// Periodic duties: worker heartbeat, recovery of jobs abandoned by a crashed worker, the
/// monotonic clock high-water mark (clock-rollback detection) and the daily usage snapshot.
/// </summary>
public sealed class Housekeeping(NpgsqlDataSource ds, JobQueue jobs, WorkerIdentity identity, ZentriqOptions options, ILogger<Housekeeping> logger) : BackgroundService
{
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await HeartbeatAsync(stoppingToken);
                var recovered = await jobs.RecoverExpiredLeasesAsync(stoppingToken);
                if (recovered > 0) logger.LogWarning("Recovered {Count} job(s) whose worker lease expired", recovered);
                await ReconcileScansAsync(stoppingToken);
                if (tick % 2 == 0) await ClockAsync(stoppingToken);
                if (tick % 20 == 0)
                    await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.UsageSnapshot, new { source = "scheduled" },
                        $"usage:{DateTimeOffset.UtcNow:yyyy-MM-dd}", Priority: 200, MaxAttempts: 5, TimeoutSeconds: 600), ct: stoppingToken);
                if (tick % 2 == 0) await ScheduledReportsAsync(stoppingToken);
                if (tick % 20 == 5) await VulnerabilityJobsAsync(stoppingToken);
                if (tick % 120 == 7) await PurgeAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Housekeeping cycle failed"); }
            tick++;
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Queues a delivery job for every scheduled report that is due and moves its next run forward.</summary>
    private async Task ScheduledReportsAsync(CancellationToken ct)
    {
        var due = new List<(Guid Id, DateTimeOffset At, string Schedule)>();
        await using (var cmd = ds.CreateCommand("""
            SELECT id, next_run_at, schedule::text FROM zentriq.report_definitions
             WHERE schedule IS NOT NULL AND (schedule ->> 'enabled')::boolean AND next_run_at <= now() LIMIT 50
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) due.Add((r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2)));
        foreach (var (id, at, json) in due)
        {
            var schedule = System.Text.Json.JsonSerializer.Deserialize<Zentriq.Infrastructure.Reports.ReportSchedule>(json) ?? new();
            await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.ReportDeliver, new { reportId = id, trigger = "schedule" },
                $"report:{id}:{at.UtcDateTime:yyyyMMddHHmm}", Priority: 150, MaxAttempts: 2, TimeoutSeconds: 1800), ct: ct);
            await using var upd = ds.CreateCommand("UPDATE zentriq.report_definitions SET next_run_at = @n WHERE id = @id");
            upd.Parameters.AddWithValue("id", id);
            upd.Parameters.AddWithValue("n", schedule.NextRun(DateTimeOffset.UtcNow));
            await upd.ExecuteNonQueryAsync(ct);
            logger.LogInformation("Scheduled report {Report} queued", id);
        }
    }

    /// <summary>Daily feed update (when automatic updates are on) and a findings re-evaluation every 30 minutes.</summary>
    private async Task VulnerabilityJobsAsync(CancellationToken ct)
    {
        await using (var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'vuln.auto_sync'"))
        {
            var v = await cmd.ExecuteScalarAsync(ct) as string;
            if (v is null || v.Trim() == "true")
                await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.VulnSync, new { source = "scheduled" }, $"vuln-sync:{DateTimeOffset.UtcNow:yyyy-MM-dd}",
                    Priority: 180, MaxAttempts: 2, TimeoutSeconds: 4 * 3600, RunAfter: DateTimeOffset.UtcNow.AddMinutes(2)), ct: ct);
        }
        var slot = DateTimeOffset.UtcNow;
        await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.VulnEvaluate, new { source = "scheduled" }, $"vuln-eval:{slot:yyyy-MM-dd-HH}:{slot.Minute / 30}",
            Priority: 190, MaxAttempts: 2, TimeoutSeconds: 1800), ct: ct);
        await using (var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'security.auto_evaluate'"))
        {
            var v = await cmd.ExecuteScalarAsync(ct) as string;
            if (v is null || v.Trim() == "true")
                await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.SecurityEvaluate, new { source = "scheduled" }, $"sec-eval:{slot:yyyy-MM-dd-HH}:{slot.Minute / 30}",
                    Priority: 190, MaxAttempts: 2, TimeoutSeconds: 1800), ct: ct);
        }
    }

    /// <summary>Report files are kept 30 days; uploaded feed files are dropped once processed (or after a day).</summary>
    private async Task PurgeAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.report_runs SET content = NULL WHERE content IS NOT NULL AND started_at < now() - interval '30 days';
            DELETE FROM zentriq.report_runs WHERE started_at < now() - interval '365 days';
            DELETE FROM zentriq.asset_changes WHERE detected_at < now() - interval '365 days';
            DELETE FROM zentriq.int_deliveries WHERE created_at < now() - interval '90 days';
            DELETE FROM zentriq.int_events e WHERE e.occurred_at < now() - interval '90 days' AND NOT EXISTS (SELECT 1 FROM zentriq.int_deliveries d WHERE d.event_id = e.id);
            DELETE FROM zentriq.sec_exceptions WHERE expires_at < now() - interval '365 days';
            UPDATE zentriq.vuln_imports SET content = NULL, status = CASE WHEN status IN ('queued','running') THEN 'failed' ELSE status END,
                   message = COALESCE(message, 'Not processed within a day.') WHERE content IS NOT NULL AND created_at < now() - interval '1 day';
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Scans whose job ended without the handler finishing them (timeout, exhausted retries).</summary>
    private async Task ReconcileScansAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.discovery_scans s
               SET status = CASE j.status WHEN 'cancelled' THEN 'cancelled' ELSE 'failed' END,
                   finished_at = now(), error = COALESCE(s.error, j.last_error)
              FROM zentriq.jobs j
             WHERE s.job_id = j.id AND s.status IN ('queued','running','cancelling') AND j.status IN ('failed','cancelled','dead')
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.worker_heartbeats (worker_id, host_name, version, started_at, last_beat_at)
            VALUES (@w, @h, @v, @s, now())
            ON CONFLICT (worker_id) DO UPDATE SET last_beat_at = now();
            DELETE FROM zentriq.worker_heartbeats WHERE last_beat_at < now() - interval '1 day';
            """);
        cmd.Parameters.AddWithValue("w", identity.Id);
        cmd.Parameters.AddWithValue("h", Environment.MachineName);
        cmd.Parameters.AddWithValue("v", options.ProductVersion);
        cmd.Parameters.AddWithValue("s", _started);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Advances install_state.last_seen_utc using this machine's clock. The DB trigger keeps the
    /// value monotonic. If the clock is behind the mark by more than the tolerance, record it and
    /// notify administrators; licensing then restricts feature APIs (see LicenseEvaluator).
    /// </summary>
    private async Task ClockAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            WITH tol AS (SELECT COALESCE((SELECT value::text::int FROM zentriq.system_settings WHERE key = 'clock.rollback_tolerance_minutes'), 1440) AS m),
            cur AS (SELECT last_seen_utc FROM zentriq.install_state WHERE id = 1)
            UPDATE zentriq.install_state s SET
                last_seen_utc = @now,
                clock_rollback_detected_at = CASE WHEN @now < cur.last_seen_utc - make_interval(mins => tol.m)
                                                  THEN COALESCE(s.clock_rollback_detected_at, @now) ELSE NULL END
            FROM cur, tol WHERE s.id = 1
            RETURNING s.clock_rollback_detected_at
            """);
        cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        var detected = await cmd.ExecuteScalarAsync(ct);
        if (detected is DateTime or DateTimeOffset)
        {
            logger.LogError("System clock rollback detected. Feature APIs are restricted until the clock is corrected.");
            await using var n = ds.CreateCommand("""
                INSERT INTO zentriq.notifications (user_id, kind, title, body)
                SELECT NULL, 'crit', 'System clock moved backwards', 'Correct the server time. Zentriq restricts feature APIs while the clock is behind its last observed time.'
                WHERE NOT EXISTS (SELECT 1 FROM zentriq.notifications WHERE title = 'System clock moved backwards' AND created_at > now() - interval '1 day')
                """);
            await n.ExecuteNonQueryAsync(ct);
        }
    }
}
