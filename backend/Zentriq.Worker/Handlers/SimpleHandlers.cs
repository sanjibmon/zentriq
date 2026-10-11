using System.Text.Json;
using Npgsql;
using Zentriq.Domain;
using Zentriq.Infrastructure.Licensing;

namespace Zentriq.Worker.Handlers;

/// <summary>Used by the installer to prove the worker can lease, run and complete a job.</summary>
public sealed class SelfTestHandler(WorkerIdentity identity) : IJobHandler
{
    public string JobType => JobTypes.SelfTest;

    public async Task<object?> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        await context.LogAsync("Self-test started.", ct: ct);
        await context.ProgressAsync(50, "Writing test event", ct);
        await context.LogAsync("Self-test completed.", ct: ct);
        return new { ok = true, worker = identity.Id, processedAt = DateTimeOffset.UtcNow };
    }
}

/// <summary>Records billable usage per workload: an auditable, reconcilable trail of license consumption.</summary>
public sealed class UsageSnapshotHandler(LicensingService licensing, NpgsqlDataSource ds) : IJobHandler
{
    public string JobType => JobTypes.UsageSnapshot;

    public async Task<object?> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var status = await licensing.GetStatusAsync(ct);
        var counts = await licensing.CountBillableAsync(null, null, ct);
        var source = JsonDocument.Parse(context.Job.Payload).RootElement.TryGetProperty("source", out var s) ? s.GetString() ?? "scheduled" : "scheduled";
        foreach (var w in Workloads.All)
        {
            await using var cmd = ds.CreateCommand("INSERT INTO zentriq.workload_usage_snapshots (workload, billable, entitled, source, details) VALUES (@w, @b, @e, @s, @d::jsonb)");
            cmd.Parameters.AddWithValue("w", w);
            cmd.Parameters.AddWithValue("b", counts.GetValueOrDefault(w));
            cmd.Parameters.AddWithValue("e", status.Limits.TryGetValue(w, out var l) ? (object)l : DBNull.Value);
            cmd.Parameters.AddWithValue("s", source);
            cmd.Parameters.AddWithValue("d", JsonSerializer.Serialize(new { licenseMode = status.Mode.ToString(), status.LicenseId }));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return new { counts };
    }
}
