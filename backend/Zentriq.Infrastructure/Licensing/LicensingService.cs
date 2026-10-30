using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zentriq.Application.Common;
using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Licensing;

public sealed record WorkloadUsage(string Workload, int Billable, int? Limit, string Title = "", bool Metric = true);

/// <summary>A license limit (IT devices, non-IT assets, cloud instances, technicians, or a per-type device limit) was reached.</summary>
public sealed class WorkloadLimitExceededException(string workload, int limit)
    : Exception(workload switch
    {
        Workloads.TotalDevices => $"IT device limit reached: the license allows {limit} IT devices and they are all in use. Retire unused devices, or extend the license.",
        Workloads.NonItAssets => $"Non-IT asset limit reached: the license allows {limit} non-IT assets and they are all in use. Dispose of unused records, or add a larger non-IT pack.",
        Workloads.CloudInstances => $"The license allows {limit} cloud and virtual instances, and the connections report more. Extend the license, or limit what the connections read (subscriptions, regions, projects).",
        Workloads.Technicians => limit == 0
            ? "Remote support is not part of this license."
            : $"Remote support is licensed for {limit} technician(s), but more users have the right to start remote sessions. Remove the right from users who do not need it (Users, Roles & Groups), or extend the license.",
        _ => $"The {workload.Replace('_', ' ')} limit ({limit}) has been reached. Retire unused assets or extend the license.",
    })
{
    public string Workload { get; } = workload;
    public int Limit { get; } = limit;
}

/// <summary>Database-backed licensing: current status, usage per licensed metric and enforcement.</summary>
public sealed class LicensingService(
    ZentriqDbContext db,
    NpgsqlDataSource dataSource,
    LicenseVerifier verifier,
    IClock clock,
    Func<AppSecrets> appSecrets)
{
    public const string WorkloadLockSql = "SELECT pg_advisory_xact_lock(hashtext('zentriq.workload_enforcement'))";

    public async Task<LicenseStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var state = await db.InstallState.AsNoTracking().FirstOrDefaultAsync(ct);
        var trialRow = await db.Trial.AsNoTracking().FirstOrDefaultAsync(ct);
        var tolerance = TimeSpan.FromMinutes(await GetSettingAsync("clock.rollback_tolerance_minutes", 1440, ct));

        TrialSnapshot? trial = null;
        if (trialRow is not null && state is not null)
        {
            var policy = JsonSerializer.Deserialize<TrialPolicy>(trialRow.Policy, JsonOptions) ?? TrialPolicy.Default;
            var ok = TrialIntegrity.Verify(appSecrets().InstallSecretBytes(), state.InstallationId, trialRow.StartedAt, trialRow.ExpiresAt, trialRow.IntegrityMac)
                     && trialRow.InstallationId == state.InstallationId;
            trial = new TrialSnapshot(trialRow.StartedAt, trialRow.ExpiresAt, policy, ok);
        }

        ActiveLicenseSnapshot? license = null;
        var rec = await db.LicenseRecords.AsNoTracking().FirstOrDefaultAsync(l => l.Status == "active", ct);
        if (rec is not null && state is not null)
        {
            // Re-verify the stored signed document; never trust database columns alone.
            var v = verifier.Verify(rec.RawDocument, state.InstallationId, clock.UtcNow, enforceValidityWindow: false);
            if (v.IsValid) license = new ActiveLicenseSnapshot(v.Payload!);
        }

        // The free tier depends on usage: count only when it can apply (no license, or the license has run out).
        var now = clock.UtcNow;
        var mayBeFree = license is null
            ? trial is null || !trial.IntegrityValid || now >= trial.ExpiresAt
            : license.Payload.LicenseType != LicenseFormats.Perpetual && now >= license.Payload.ExpiresAt.AddDays(license.Payload.GraceDays);
        var usage = mayBeFree && state?.InitializedAt is not null ? await CountUsageAsync(null, null, ct) : null;

        return LicenseEvaluator.Evaluate(new LicenseInputs(
            state?.InitializedAt is not null, trial, license, now,
            state?.LastSeenUtc ?? now, tolerance, ProductInfo.ReleaseDate, usage)).RestrictTo(await InstalledSectionsAsync(ct));
    }

    /// <summary>Setting written by setup (Standard / Advanced installation): the product sections installed on this server.</summary>
    public const string SectionsSetting = "product.sections";

    /// <summary>The product sections installed on this server; all of them when setup did not record a choice.</summary>
    public async Task<IReadOnlyList<string>> InstalledSectionsAsync(CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = @k");
        cmd.Parameters.AddWithValue("k", SectionsSetting);
        return ProductSections.Parse(await cmd.ExecuteScalarAsync(ct) as string);
    }

    /// <summary>Usage per licensed metric (IT devices, non-IT assets, cloud instances, technicians), then IT devices per type.</summary>
    public async Task<IReadOnlyList<WorkloadUsage>> GetUsageAsync(CancellationToken ct = default, LicenseStatus? status = null)
    {
        status ??= await GetStatusAsync(ct);
        var usage = await CountUsageAsync(null, null, ct);
        var types = await CountBillableAsync(null, null, ct);
        var rows = Workloads.Metrics.Select(m => new WorkloadUsage(m, usage.GetValueOrDefault(m), status.Limits.TryGetValue(m, out var l) ? l : null, Workloads.Title(m))).ToList();
        rows.AddRange(Workloads.All.Select(w => new WorkloadUsage(w, types.GetValueOrDefault(w), status.Limits.TryGetValue(w, out var l) ? l : null, Workloads.Title(w), Metric: false)));
        return rows;
    }

    /// <summary>
    /// IT devices that count: status 'active', not merged, seen within licensing.billable_seen_within_days, not planned /
    /// on order / in stock (Asset Lifecycle), and not a virtual machine of a cloud or virtualization connection (those count
    /// as cloud instances). Grouped by device type.
    /// </summary>
    public async Task<Dictionary<string, int>> CountBillableAsync(NpgsqlConnection? conn, NpgsqlTransaction? tx, CancellationToken ct = default)
    {
        var days = await GetSettingAsync("licensing.billable_seen_within_days", 30, conn, tx, ct);
        const string sql = """
            SELECT CASE category WHEN 'endpoint' THEN 'endpoint'
                                 WHEN 'server' THEN 'server' WHEN 'virtual_host' THEN 'server'
                                 WHEN 'network_device' THEN 'network_device'
                                 ELSE 'other' END AS workload,
                   count(*)::int
              FROM zentriq.assets
             WHERE status = 'active' AND merged_into IS NULL
               AND last_seen_at >= now() - make_interval(days => @d)
               -- Asset Lifecycle: items planned, on order or waiting in stock are not in use
               AND NOT EXISTS (SELECT 1 FROM zentriq.asset_lifecycle l WHERE l.asset_id = assets.id AND l.stage IN ('planned','on_order','in_stock'))
               -- A virtual machine from a cloud / virtualization connection counts as a cloud instance instead
               AND NOT EXISTS (SELECT 1 FROM zentriq.cloud_resources c WHERE c.asset_id = assets.id AND c.kind = 'vm' AND c.deleted_at IS NULL)
             GROUP BY 1
            """;
        await using var cmd = conn is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("d", days);
        var result = new Dictionary<string, int>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result[r.GetString(0)] = r.GetInt32(1);
        return result;
    }

    /// <summary>Usage per licensed metric (see <see cref="Workloads.Metrics"/>).</summary>
    public async Task<Dictionary<string, int>> CountUsageAsync(NpgsqlConnection? conn, NpgsqlTransaction? tx, CancellationToken ct = default)
    {
        var devices = (await CountBillableAsync(conn, tx, ct)).Values.Sum();
        const string sql = """
            SELECT (SELECT count(*) FROM zentriq.nonit_assets WHERE status <> 'disposed')::int,
                   (SELECT count(*) FROM zentriq.cloud_resources WHERE kind = 'vm' AND deleted_at IS NULL)::int,
                   (SELECT count(DISTINCT u.id) FROM zentriq.users u
                      JOIN zentriq.user_role_grants g ON g.user_id = u.id
                      JOIN zentriq.role_permissions rp ON rp.role_id = g.role_id
                     WHERE u.status = 'active' AND rp.permission_code = 'remote.start')::int
            """;
        await using var cmd = conn is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new Dictionary<string, int>
        {
            [Workloads.TotalDevices] = devices, [Workloads.NonItAssets] = r.GetInt32(0),
            [Workloads.CloudInstances] = r.GetInt32(1), [Workloads.Technicians] = r.GetInt32(2),
        };
    }

    /// <summary>
    /// Must be called inside the transaction that makes an asset billable. Serialises concurrent enrolments with a
    /// transaction-scoped advisory lock, so two discoveries cannot both take the last remaining slot. Licenses allow a
    /// configurable overage grace (default 5 %); the trial and the free tier are exact.
    /// </summary>
    public async Task EnsureCapacityAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string workload, int adding, LicenseStatus status, CancellationToken ct = default)
    {
        await using (var l = new NpgsqlCommand(WorkloadLockSql, conn, tx)) await l.ExecuteNonQueryAsync(ct);
        var hasWorkloadLimit = status.Limits.TryGetValue(workload, out var limit);
        var hasTotal = status.Limits.TryGetValue(Workloads.TotalDevices, out var totalLimit);
        if (!hasWorkloadLimit && !hasTotal) return; // unlimited
        var counts = await CountBillableAsync(conn, tx, ct);
        var grace = status.ExactLimits ? 0 : await GetSettingAsync("licensing.overage_grace_percent", 5, conn, tx, ct);
        if (hasWorkloadLimit && counts.GetValueOrDefault(workload) + adding > limit + (int)Math.Floor(limit * grace / 100.0))
            throw new WorkloadLimitExceededException(workload, limit);
        if (hasTotal && counts.Values.Sum() + adding > totalLimit + (int)Math.Floor(totalLimit * grace / 100.0))
            throw new WorkloadLimitExceededException(Workloads.TotalDevices, totalLimit);
    }

    /// <summary>
    /// Non-IT assets, cloud instances or technicians: throws when <paramref name="adding"/> more would exceed the license
    /// (plus the overage grace for licenses). With a connection and transaction, also takes the enforcement lock.
    /// </summary>
    public async Task EnsureMetricAsync(string metric, int adding, LicenseStatus status, NpgsqlConnection? conn = null, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        if (!status.Limits.TryGetValue(metric, out var limit)) return;   // unlimited
        if (conn is not null) await using (var l = new NpgsqlCommand(WorkloadLockSql, conn, tx)) await l.ExecuteNonQueryAsync(ct);
        var used = (await CountUsageAsync(conn, tx, ct)).GetValueOrDefault(metric);
        var grace = status.ExactLimits || limit == 0 ? 0 : await GetSettingAsync("licensing.overage_grace_percent", 5, conn, tx, ct);
        if (used + adding > limit + (int)Math.Floor(limit * grace / 100.0))
            throw new WorkloadLimitExceededException(metric, limit);
    }

    public Task<int> GetSettingAsync(string key, int fallback, CancellationToken ct = default) => GetSettingAsync(key, fallback, null, null, ct);

    /// <summary>Thread-safe (raw SQL, no DbContext) so it can be used from parallel discovery tasks.</summary>
    public async Task<int> GetSettingAsync(string key, int fallback, NpgsqlConnection? conn, NpgsqlTransaction? tx, CancellationToken ct = default)
    {
        const string sql = "SELECT value::text FROM zentriq.system_settings WHERE key = @k";
        await using var cmd = conn is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("k", key);
        var v = await cmd.ExecuteScalarAsync(ct) as string;
        return v is not null && int.TryParse(v.Trim('"'), out var n) ? n : fallback;
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
