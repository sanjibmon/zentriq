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
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Housekeeping cycle failed"); }
            tick++;
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { break; }
        }
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
