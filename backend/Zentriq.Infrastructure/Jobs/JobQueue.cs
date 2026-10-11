using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Jobs;

public sealed record LeasedJob(Guid Id, string JobType, string Payload, int Attempts, int MaxAttempts, int TimeoutSeconds);

public sealed record EnqueueRequest(
    string JobType,
    object? Payload = null,
    string? IdempotencyKey = null,
    int Priority = 100,
    int MaxAttempts = 3,
    int TimeoutSeconds = 3600,
    DateTimeOffset? RunAfter = null,
    Guid? CreatedBy = null);

/// <summary>
/// Durable PostgreSQL-backed job queue. Jobs survive browser sessions and service restarts:
/// a worker leases a job (FOR UPDATE SKIP LOCKED), renews the lease while running, and
/// <see cref="RecoverExpiredLeasesAsync"/> requeues jobs whose worker died.
/// </summary>
public sealed class JobQueue(NpgsqlDataSource dataSource)
{
    public async Task<Guid> EnqueueAsync(EnqueueRequest r, NpgsqlConnection? conn = null, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO zentriq.jobs (job_type, payload, idempotency_key, priority, max_attempts, timeout_seconds, run_after, created_by)
            VALUES (@t, @p, @k, @pr, @ma, @to, COALESCE(@ra, now()), @cb)
            ON CONFLICT (idempotency_key) DO UPDATE SET idempotency_key = EXCLUDED.idempotency_key
            RETURNING id
            """;
        await using var cmd = conn is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("t", r.JobType);
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(r.Payload ?? new { }) });
        cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = (object?)r.IdempotencyKey ?? DBNull.Value });
        cmd.Parameters.AddWithValue("pr", r.Priority);
        cmd.Parameters.AddWithValue("ma", r.MaxAttempts);
        cmd.Parameters.AddWithValue("to", r.TimeoutSeconds);
        cmd.Parameters.Add(new NpgsqlParameter("ra", NpgsqlDbType.TimestampTz) { Value = (object?)r.RunAfter?.ToUniversalTime() ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("cb", NpgsqlDbType.Uuid) { Value = (object?)r.CreatedBy ?? DBNull.Value });
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Leases the next runnable job of the given types (or excluding maintenance jobs when maintenance=false).</summary>
    public async Task<LeasedJob?> LeaseNextAsync(string workerId, TimeSpan lease, bool maintenance, CancellationToken ct = default)
    {
        const string sql = """
            WITH next AS (
                SELECT id FROM zentriq.jobs
                WHERE status = 'queued' AND run_after <= now()
                  AND (job_type LIKE 'maintenance.%') = @maint
                ORDER BY priority, run_after, created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            UPDATE zentriq.jobs j
               SET status = 'running', attempts = j.attempts + 1, lease_owner = @w,
                   lease_expires_at = now() + make_interval(secs => @lease),
                   started_at = COALESCE(j.started_at, now()), last_error = NULL
              FROM next WHERE j.id = next.id
            RETURNING j.id, j.job_type, j.payload::text, j.attempts, j.max_attempts, j.timeout_seconds
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("w", workerId);
        cmd.Parameters.AddWithValue("lease", lease.TotalSeconds);
        cmd.Parameters.AddWithValue("maint", maintenance);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new LeasedJob(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5));
    }

    /// <summary>Renews the lease and returns whether cancellation was requested.</summary>
    public async Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE zentriq.jobs SET lease_expires_at = now() + make_interval(secs => @l) WHERE id = @id AND lease_owner = @w AND status = 'running' RETURNING cancel_requested");
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("w", workerId);
        cmd.Parameters.AddWithValue("l", lease.TotalSeconds);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is not bool b || b; // lease lost => treat as cancelled
    }

    public async Task ReportProgressAsync(Guid jobId, int pct, string? text, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("UPDATE zentriq.jobs SET progress_pct = @p, progress_text = @t WHERE id = @id");
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("p", (short)Math.Clamp(pct, 0, 100));
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = (object?)text ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task LogAsync(Guid jobId, string level, string message, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("INSERT INTO zentriq.job_events (job_id, level, message) VALUES (@id, @l, @m)");
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("l", level);
        cmd.Parameters.AddWithValue("m", Application.Security.Redactor.Redact(message));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteAsync(Guid jobId, string workerId, object? result, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE zentriq.jobs SET status = 'succeeded', progress_pct = 100, result = @r, finished_at = now(),
                   lease_owner = NULL, lease_expires_at = NULL
            WHERE id = @id AND lease_owner = @w
            """);
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("w", workerId);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(result ?? new { }) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkCancelledAsync(Guid jobId, string workerId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE zentriq.jobs SET status = 'cancelled', finished_at = now(), lease_owner = NULL, lease_expires_at = NULL
            WHERE id = @id AND lease_owner = @w
            """);
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("w", workerId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Retries with exponential backoff (30 s, 2 min, 8 min...) until max_attempts, then 'failed'.</summary>
    public async Task FailAsync(Guid jobId, string workerId, string error, bool retryable, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE zentriq.jobs SET
                status = CASE WHEN @retry AND attempts < max_attempts THEN 'queued' ELSE 'failed' END,
                run_after = CASE WHEN @retry AND attempts < max_attempts THEN now() + make_interval(secs => 30 * power(4, attempts - 1)) ELSE run_after END,
                finished_at = CASE WHEN @retry AND attempts < max_attempts THEN NULL ELSE now() END,
                last_error = @e, lease_owner = NULL, lease_expires_at = NULL
            WHERE id = @id AND lease_owner = @w
            """);
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("w", workerId);
        cmd.Parameters.AddWithValue("e", Application.Security.Redactor.Redact(error.Length > 4000 ? error[..4000] : error));
        cmd.Parameters.AddWithValue("retry", retryable);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>On graceful shutdown: return the job to the queue without consuming an attempt.</summary>
    public async Task ReleaseAsync(Guid jobId, string workerId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE zentriq.jobs SET status = 'queued', attempts = GREATEST(attempts - 1, 0), lease_owner = NULL, lease_expires_at = NULL
            WHERE id = @id AND lease_owner = @w AND status = 'running'
            """);
        cmd.Parameters.AddWithValue("id", jobId);
        cmd.Parameters.AddWithValue("w", workerId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Requeues (or fails) running jobs whose lease expired because their worker stopped unexpectedly.</summary>
    public async Task<int> RecoverExpiredLeasesAsync(CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE zentriq.jobs SET
                status = CASE WHEN cancel_requested THEN 'cancelled' WHEN attempts < max_attempts THEN 'queued' ELSE 'failed' END,
                finished_at = CASE WHEN cancel_requested OR attempts >= max_attempts THEN now() ELSE NULL END,
                last_error = COALESCE(last_error, 'Worker lease expired (worker stopped or lost connection).'),
                lease_owner = NULL, lease_expires_at = NULL
            WHERE status = 'running' AND lease_expires_at < now()
            """);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Cancels a queued job immediately or flags a running job for cooperative cancellation.</summary>
    public async Task<string?> RequestCancelAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE zentriq.jobs SET
                cancel_requested = true,
                status = CASE WHEN status = 'queued' THEN 'cancelled' ELSE status END,
                finished_at = CASE WHEN status = 'queued' THEN now() ELSE finished_at END
            WHERE id = @id AND status IN ('queued','running')
            RETURNING status
            """);
        cmd.Parameters.AddWithValue("id", jobId);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
