using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Inventory;

namespace Zentriq.Infrastructure.Experience;

/// <summary>A problem found on a device, shown on its Experience tab and in the fleet view.</summary>
public sealed record ExperienceIssue(string Kind, string Severity, string Text, int Points);

public sealed record ExperienceInputs(
    long? BootMs, long? LogonMs, int Crashes, int Hangs, int Bugchecks, int Shutdowns, int ServiceFailures,
    double? CpuHighPercent, double? MemHighPercent, double? SystemFreePercent, double? BatteryHealth, double? UptimeDays);

public sealed record ExperienceScore(int Score, string Rating, List<ExperienceIssue> Issues);

/// <summary>
/// Stores what the agent reports for software metering and Device Experience (replacing the reported days, so a report
/// sent twice changes nothing) and scores the device: 100 minus points for slow starts, crashes and hangs, blue screens,
/// unexpected shutdowns, service failures, sustained processor or memory pressure, a full system drive, a worn battery
/// and a device that has not restarted for weeks — over the last 7 days.
/// </summary>
public static class ExperienceStore
{
    public const int WindowDays = 7;

    public static async Task RecordAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, InventoryDocument doc, CancellationToken ct)
    {
        if (doc.Usage is { } usage) await RecordUsageAsync(conn, tx, assetId, usage, ct);
        if (doc.Experience is { } exp)
        {
            await RecordExperienceAsync(conn, tx, assetId, exp, ct);
            double? uptime = doc.Os.LastBoot is { } boot && boot < DateTimeOffset.UtcNow ? Math.Round((DateTimeOffset.UtcNow - boot).TotalDays, 1) : null;
            await ScoreAsync(conn, tx, assetId, exp.SystemFreePercent, exp.BatteryHealth, uptime, ct);
        }
    }

    private static DateOnly? Day(string s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)) ? d : null;

    private static string? T(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Replace("\0", "").Trim();
        return t.Length == 0 ? null : t.Length > max ? t[..max] : t;
    }

    private static async Task RecordUsageAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, UsageReport usage, CancellationToken ct)
    {
        var items = usage.Items
            .Select(i => (Day: Day(i.Day), i))
            .Where(x => x.Day is not null && !string.IsNullOrWhiteSpace(x.i.Path))
            .GroupBy(x => (x.Day!.Value, Path: T(x.i.Path, 1000)!, User: T(x.i.User, 300) ?? ""))
            .Select(g => (g.Key.Value, g.Key.Path, g.Key.User, I: g.First().i, Minutes: Math.Min(1440, g.Sum(x => Math.Max(0, x.i.Minutes))), Launches: Math.Min(100000, g.Sum(x => Math.Max(0, x.i.Launches)))))
            .Take(20000).ToList();
        var days = items.Select(i => i.Value).Distinct().ToArray();
        if (days.Length > 0)
        {
            await using (var del = new NpgsqlCommand("DELETE FROM zentriq.software_usage WHERE asset_id = @a AND day = ANY (@d)", conn, tx))
            {
                del.Parameters.AddWithValue("a", assetId);
                del.Parameters.AddWithValue("d", days);
                await del.ExecuteNonQueryAsync(ct);
            }
            await using var ins = new NpgsqlCommand("""
                INSERT INTO zentriq.software_usage (asset_id, day, path, exe, product, publisher, version, user_name, minutes, launches)
                SELECT @a, * FROM unnest(@day, @path, @exe, @product, @publisher, @version, @user, @minutes, @launches)
                """, conn, tx);
            ins.Parameters.AddWithValue("a", assetId);
            ins.Parameters.AddWithValue("day", items.Select(i => i.Value).ToArray());
            ins.Parameters.AddWithValue("path", items.Select(i => i.Path).ToArray());
            ins.Parameters.AddWithValue("exe", items.Select(i => T(i.I.Exe, 260) ?? System.IO.Path.GetFileName(i.Path.Replace('\\', '/'))).ToArray());
            ins.Parameters.Add(new NpgsqlParameter("product", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = items.Select(i => (object?)T(i.I.Product, 200) ?? DBNull.Value).ToArray() });
            ins.Parameters.Add(new NpgsqlParameter("publisher", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = items.Select(i => (object?)T(i.I.Publisher, 200) ?? DBNull.Value).ToArray() });
            ins.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = items.Select(i => (object?)T(i.I.Version, 100) ?? DBNull.Value).ToArray() });
            ins.Parameters.AddWithValue("user", items.Select(i => i.User).ToArray());
            ins.Parameters.AddWithValue("minutes", items.Select(i => i.Minutes).ToArray());
            ins.Parameters.AddWithValue("launches", items.Select(i => i.Launches).ToArray());
            await ins.ExecuteNonQueryAsync(ct);
        }
        if (Day(usage.Since) is { } since)
        {
            await using var dev = new NpgsqlCommand("""
                INSERT INTO zentriq.software_usage_devices (asset_id, since, reported_at) VALUES (@a, @s, now())
                ON CONFLICT (asset_id) DO UPDATE SET since = LEAST(zentriq.software_usage_devices.since, EXCLUDED.since), reported_at = now()
                """, conn, tx);
            dev.Parameters.AddWithValue("a", assetId);
            dev.Parameters.AddWithValue("s", since);
            await dev.ExecuteNonQueryAsync(ct);
        }
    }

    private static readonly HashSet<string> Kinds = ["boot", "crash", "hang", "bugcheck", "shutdown", "service", "oom"];

    private static async Task RecordExperienceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, ExperienceReport exp, CancellationToken ct)
    {
        foreach (var d in exp.Days.Take(40))
        {
            if (Day(d.Day) is not { } day || d.Samples <= 0) continue;
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.device_experience_days (asset_id, day, samples, cpu_avg, cpu_high, mem_avg, mem_high) VALUES (@a, @d, @n, @ca, @ch, @ma, @mh)
                ON CONFLICT (asset_id, day) DO UPDATE SET samples = EXCLUDED.samples, cpu_avg = EXCLUDED.cpu_avg, cpu_high = EXCLUDED.cpu_high,
                    mem_avg = EXCLUDED.mem_avg, mem_high = EXCLUDED.mem_high
                """, conn, tx);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.AddWithValue("d", day);
            cmd.Parameters.AddWithValue("n", Math.Min(d.Samples, 1440));
            cmd.Parameters.AddWithValue("ca", (float)Math.Clamp(d.CpuAvg, 0, 100));
            cmd.Parameters.AddWithValue("ch", Math.Clamp(d.CpuHigh, 0, 1440));
            cmd.Parameters.AddWithValue("ma", (float)Math.Clamp(d.MemAvg, 0, 100));
            cmd.Parameters.AddWithValue("mh", Math.Clamp(d.MemHigh, 0, 1440));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var events = exp.Events.Where(e => Kinds.Contains(e.Kind) && e.At > DateTimeOffset.UtcNow.AddDays(-60) && e.At < DateTimeOffset.UtcNow.AddDays(1)).Take(1500).ToList();
        if (events.Count == 0) return;
        await using var ins = new NpgsqlCommand("""
            INSERT INTO zentriq.device_experience_events (asset_id, at, kind, app, version, detail, ms, ms2)
            SELECT @a, * FROM unnest(@at, @kind, @app, @version, @detail, @ms, @ms2)
            ON CONFLICT (asset_id, kind, at, COALESCE(app, '')) DO NOTHING
            """, conn, tx);
        ins.Parameters.AddWithValue("a", assetId);
        ins.Parameters.AddWithValue("at", events.Select(e => e.At.ToUniversalTime()).ToArray());
        ins.Parameters.AddWithValue("kind", events.Select(e => e.Kind).ToArray());
        ins.Parameters.Add(new NpgsqlParameter("app", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = events.Select(e => (object?)T(e.App, 200) ?? DBNull.Value).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = events.Select(e => (object?)T(e.Version, 100) ?? DBNull.Value).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("detail", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = events.Select(e => (object?)T(e.Detail, 400) ?? DBNull.Value).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("ms", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = events.Select(e => e.Ms is > 0 and < 86_400_000 ? (object)e.Ms.Value : DBNull.Value).ToArray() });
        ins.Parameters.Add(new NpgsqlParameter("ms2", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = events.Select(e => e.Ms2 is > 0 and < 86_400_000 ? (object)e.Ms2.Value : DBNull.Value).ToArray() });
        await ins.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Scores the device from what is stored for the last <see cref="WindowDays"/> days.</summary>
    public static async Task<ExperienceScore> ScoreAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid assetId, double? systemFree, double? battery, double? uptime, CancellationToken ct)
    {
        ExperienceInputs inputs;
        await using (var cmd = new NpgsqlCommand($"""
            WITH e AS (SELECT * FROM zentriq.device_experience_events WHERE asset_id = @a AND at > now() - interval '{WindowDays} days'),
                 d AS (SELECT * FROM zentriq.device_experience_days WHERE asset_id = @a AND day > current_date - {WindowDays})
            SELECT (SELECT percentile_disc(0.5) WITHIN GROUP (ORDER BY ms) FROM e WHERE kind = 'boot' AND ms IS NOT NULL),
                   (SELECT percentile_disc(0.5) WITHIN GROUP (ORDER BY ms2) FROM e WHERE kind = 'boot' AND ms2 IS NOT NULL),
                   (SELECT count(*) FROM e WHERE kind = 'crash'), (SELECT count(*) FROM e WHERE kind = 'hang'),
                   (SELECT count(*) FROM e WHERE kind = 'bugcheck'), (SELECT count(*) FROM e WHERE kind = 'shutdown'),
                   (SELECT count(*) FROM e WHERE kind IN ('service','oom')),
                   (SELECT CASE WHEN sum(samples) >= 30 THEN 100.0 * sum(cpu_high) / sum(samples) END FROM d),
                   (SELECT CASE WHEN sum(samples) >= 30 THEN 100.0 * sum(mem_high) / sum(samples) END FROM d)
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("a", assetId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            long? L(int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
            double? D(int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));
            inputs = new ExperienceInputs(L(0), L(1), (int)L(2)!, (int)L(3)!, (int)L(4)!, (int)L(5)!, (int)L(6)!, D(7), D(8), systemFree, battery, uptime);
        }
        var s = Score(inputs);
        await using (var up = new NpgsqlCommand("""
            INSERT INTO zentriq.device_experience (asset_id, score, rating, boot_ms, logon_ms, crashes, hangs, bugchecks, shutdowns, service_failures,
                   cpu_high_percent, mem_high_percent, system_free_percent, battery_health, uptime_days, issues, computed_at)
            VALUES (@a, @s, @r, @b, @l, @c, @h, @bc, @sd, @sf, @cpu, @mem, @free, @bat, @up, @i, now())
            ON CONFLICT (asset_id) DO UPDATE SET score = EXCLUDED.score, rating = EXCLUDED.rating, boot_ms = EXCLUDED.boot_ms, logon_ms = EXCLUDED.logon_ms,
                   crashes = EXCLUDED.crashes, hangs = EXCLUDED.hangs, bugchecks = EXCLUDED.bugchecks, shutdowns = EXCLUDED.shutdowns,
                   service_failures = EXCLUDED.service_failures, cpu_high_percent = EXCLUDED.cpu_high_percent, mem_high_percent = EXCLUDED.mem_high_percent,
                   system_free_percent = EXCLUDED.system_free_percent, battery_health = EXCLUDED.battery_health, uptime_days = EXCLUDED.uptime_days,
                   issues = EXCLUDED.issues, computed_at = now();
            INSERT INTO zentriq.device_experience_history (asset_id, day, score) VALUES (@a, current_date, @s)
            ON CONFLICT (asset_id, day) DO UPDATE SET score = EXCLUDED.score;
            """, conn, tx))
        {
            object N<T>(T? v) where T : struct => v.HasValue ? (object)v.Value : DBNull.Value;
            up.Parameters.AddWithValue("a", assetId);
            up.Parameters.AddWithValue("s", s.Score);
            up.Parameters.AddWithValue("r", s.Rating);
            up.Parameters.AddWithValue("b", N(inputs.BootMs));
            up.Parameters.AddWithValue("l", N(inputs.LogonMs));
            up.Parameters.AddWithValue("c", inputs.Crashes);
            up.Parameters.AddWithValue("h", inputs.Hangs);
            up.Parameters.AddWithValue("bc", inputs.Bugchecks);
            up.Parameters.AddWithValue("sd", inputs.Shutdowns);
            up.Parameters.AddWithValue("sf", inputs.ServiceFailures);
            up.Parameters.AddWithValue("cpu", N(inputs.CpuHighPercent is { } c ? (float)Math.Round(c, 1) : (float?)null));
            up.Parameters.AddWithValue("mem", N(inputs.MemHighPercent is { } m ? (float)Math.Round(m, 1) : (float?)null));
            up.Parameters.AddWithValue("free", N(systemFree is { } f ? (float)f : (float?)null));
            up.Parameters.AddWithValue("bat", N(battery is { } b ? (float)b : (float?)null));
            up.Parameters.AddWithValue("up", N(uptime is { } u ? (float)u : (float?)null));
            up.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(s.Issues, JsonOptions) });
            await up.ExecuteNonQueryAsync(ct);
        }
        return s;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Duration(long ms)
    {
        var s = (int)Math.Round(ms / 1000.0);
        return s >= 60 ? $"{s / 60} min {s % 60} s" : $"{s} s";
    }

    /// <summary>The score: 100 minus the points of each problem (never below 0). Good from 80, fair from 60.</summary>
    public static ExperienceScore Score(ExperienceInputs x)
    {
        var issues = new List<ExperienceIssue>();
        void Add(string kind, int points, string text) { if (points > 0) issues.Add(new(kind, points >= 10 ? "crit" : "warn", text, points)); }

        if (x.BootMs is { } boot)
            Add("boot", boot > 120_000 ? 20 : boot > 60_000 ? 12 : boot > 30_000 ? 5 : 0, $"Slow start: {Duration(boot)} until the desktop appears (median)");
        if (x.LogonMs is { } logon)
            Add("logon", logon > 120_000 ? 8 : logon > 60_000 ? 4 : 0, $"Slow to become usable: {Duration(logon)} after the desktop appears (median)");
        Add("crash", Math.Min(25, 4 * x.Crashes), $"{x.Crashes} application crash(es)");
        Add("hang", Math.Min(15, 2 * x.Hangs), $"{x.Hangs} application(s) stopped responding");
        Add("bugcheck", Math.Min(30, 15 * x.Bugchecks), $"{x.Bugchecks} blue screen(s)");
        Add("shutdown", Math.Min(15, 5 * x.Shutdowns), $"{x.Shutdowns} unexpected shutdown(s)");
        Add("service", Math.Min(10, 2 * x.ServiceFailures), $"{x.ServiceFailures} service failure(s) or out-of-memory kill(s)");
        if (x.CpuHighPercent is { } cpu)
            Add("cpu", cpu > 20 ? 12 : cpu > 10 ? 6 : 0, $"Processor at 90 % or more {cpu:0.#} % of the time");
        if (x.MemHighPercent is { } mem)
            Add("memory", mem > 20 ? 12 : mem > 10 ? 6 : 0, $"Memory 90 % or more in use {mem:0.#} % of the time");
        if (x.SystemFreePercent is { } free)
            Add("disk", free < 5 ? 15 : free < 10 ? 8 : 0, $"System drive almost full: {free:0.#} % free");
        if (x.BatteryHealth is { } bat)
            Add("battery", bat < 60 ? 8 : bat < 80 ? 3 : 0, $"Battery worn: {bat:0} % of its design capacity");
        if (x.UptimeDays is { } up)
            Add("uptime", up > 30 ? 5 : up > 14 ? 2 : 0, $"Not restarted for {up:0} days");

        var score = Math.Max(0, 100 - issues.Sum(i => i.Points));
        return new(score, score >= 80 ? "good" : score >= 60 ? "fair" : "poor", issues.OrderByDescending(i => i.Points).ToList());
    }
}
