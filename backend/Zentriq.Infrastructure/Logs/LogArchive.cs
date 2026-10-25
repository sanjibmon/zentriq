using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Logs;

/// <summary>Scheduled log exports: every 3, 6 or 12 months, for the calendar quarter, half-year or year that just ended.</summary>
public sealed class LogExportSchedule
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("everyMonths")] public int EveryMonths { get; set; } = 3;
    [JsonPropertyName("includeAudit")] public bool IncludeAudit { get; set; } = true;
    [JsonPropertyName("includeActivity")] public bool IncludeActivity { get; set; } = true;

    public string? Validate() =>
        EveryMonths is not (3 or 6 or 12) ? "Choose every 3, 6 or 12 months." :
        !IncludeAudit && !IncludeActivity ? "Choose the audit log, the activity log or both." : null;

    /// <summary>
    /// The last period that has ended at <paramref name="now"/>: quarters (Jan–Mar …), half-years (Jan–Jun, Jul–Dec) or calendar
    /// years, in the server's time zone. Returned as instants: from (inclusive) and to (exclusive).
    /// </summary>
    public (DateTimeOffset From, DateTimeOffset To) LastPeriod(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var startMonth = (local.Month - 1) / EveryMonths * EveryMonths + 1;
        var end = new DateTime(local.Year, startMonth, 1);
        var start = end.AddMonths(-EveryMonths);
        return (LogSettings.At(start, zone), LogSettings.At(end, zone));
    }

    public string Describe() => !Enabled ? "Off" : EveryMonths switch
    {
        3 => "Every 3 months (each quarter)",
        6 => "Every 6 months (each half-year)",
        _ => "Every year",
    };
}

/// <summary>Retention and export of the audit log and the device activity log (system_settings 'logs.settings').</summary>
public sealed class LogSettings
{
    public const string Key = "logs.settings";
    /// <summary>Retention choices in days (0 = keep for ever). Shorter than 90 days is not allowed.</summary>
    public static readonly int[] RetentionChoices = [90, 180, 365, 730, 1095, 1825, 2555, 3650, 0];

    [JsonPropertyName("auditRetentionDays")] public int AuditRetentionDays { get; set; } = 365;
    [JsonPropertyName("activityRetentionDays")] public int ActivityRetentionDays { get; set; } = 365;
    /// <summary>Write entries to an archive file before they are deleted.</summary>
    [JsonPropertyName("archiveBeforeDelete")] public bool ArchiveBeforeDelete { get; set; } = true;
    [JsonPropertyName("schedule")] public LogExportSchedule Schedule { get; set; } = new();
    /// <summary>A backup destination that archives are also copied to (null = only the server's Exports folder).</summary>
    [JsonPropertyName("destinationId")] public Guid? DestinationId { get; set; }

    public string? Validate()
    {
        if (!RetentionChoices.Contains(AuditRetentionDays)) return "Choose how long the audit log is kept.";
        if (!RetentionChoices.Contains(ActivityRetentionDays)) return "Choose how long the activity log is kept.";
        return Schedule.Validate();
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static LogSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new LogSettings();
        try { return JsonSerializer.Deserialize<LogSettings>(json, Json) ?? new LogSettings(); }
        catch (JsonException) { return new LogSettings(); }
    }
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static DateTimeOffset At(DateTime local, TimeZoneInfo zone)
    {
        var l = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(l)) l = l.AddMinutes(30);
        return new DateTimeOffset(l, zone.GetUtcOffset(l)).ToUniversalTime();
    }

    /// <summary>
    /// Entries before this instant are deleted: the start of the month in which the retention ends (so deletion — and the
    /// archive made before it — happens once a month, a whole month at a time). Null when kept for ever.
    /// </summary>
    public static DateTimeOffset? Cutoff(int retentionDays, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (retentionDays <= 0) return null;
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(now.AddDays(-retentionDays), zone).DateTime;
        return At(new DateTime(local.Year, local.Month, 1), zone);
    }
}

public sealed record LogArchiveResult(string File, long Size, string Sha256, long AuditRows, long ActivityRows);

/// <summary>
/// Writes the audit log and / or the activity log of a period to a zip: audit.csv, activity.csv (UTF-8 with BOM, ISO 8601
/// UTC times) and manifest.json with row counts, SHA-256 checksums of each file and, for the audit log, the first and last
/// entry with their chain hashes, so the archive can be checked against the tamper-evident log.
/// </summary>
public static class LogArchiver
{
    public const string AuditHeader = "id,occurred_at_utc,actor,source_ip,action,target,outcome,detail,hash,prev_hash";
    public const string ActivityHeader = "id,requested_at_utc,completed_at_utc,device,ip,kind,operation,target,argument,status,result,detail,requested_by,channel";

    public const string AuditSql = """
        SELECT id, occurred_at, actor_name, source_ip, action, target, outcome, detail::text, encode(hash, 'hex'), encode(prev_hash, 'hex')
          FROM zentriq.audit_events WHERE occurred_at >= @f AND occurred_at < @t ORDER BY id
        """;
    public const string ActivitySql = """
        SELECT x.id::text, x.requested_at, x.completed_at, COALESCE(a.hostname, ''), COALESCE(host(a.primary_ip), ''), x.kind, x.operation, x.target, x.argument,
               x.status, x.result_state, x.detail, u.display_name, x.channel
          FROM zentriq.asset_actions x LEFT JOIN zentriq.assets a ON a.id = x.asset_id LEFT JOIN zentriq.users u ON u.id = x.requested_by
         WHERE x.requested_at >= @f AND x.requested_at < @t ORDER BY x.requested_at, x.id
        """;

    /// <summary>A CSV cell: quoted when needed; cells that a spreadsheet would run as a formula get an apostrophe.</summary>
    public static string Cell(object? v)
    {
        var s = v switch
        {
            null or DBNull => "",
            DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            DateTimeOffset d => d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>Writes the rows of a query as CSV; returns the row count and, for the audit log, the first / last id and hash.</summary>
    public static async Task<(long Rows, long? FirstId, long? LastId, string? LastHash)> WriteCsvAsync(NpgsqlDataSource ds, string sql, string header,
        DateTimeOffset from, DateTimeOffset to, Stream output, CancellationToken ct, bool audit = false)
    {
        await using var w = new StreamWriter(output, new UTF8Encoding(true), 1 << 16, leaveOpen: true);
        await w.WriteLineAsync(header);
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
        cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.TimestampTz) { Value = from.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.TimestampTz) { Value = to.ToUniversalTime() });
        long rows = 0; long? first = null, last = null; string? lastHash = null;
        await using var r = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);
        var values = new object?[r.FieldCount];
        var sb = new StringBuilder();
        while (await r.ReadAsync(ct))
        {
            sb.Clear();
            for (var i = 0; i < r.FieldCount; i++)
            {
                values[i] = await r.IsDBNullAsync(i, ct) ? null : r.GetValue(i);
                if (i > 0) sb.Append(',');
                sb.Append(Cell(values[i]));
            }
            await w.WriteLineAsync(sb.ToString());
            rows++;
            if (audit && values[0] is long id) { first ??= id; last = id; lastHash = values[8] as string; }
        }
        await w.FlushAsync(ct);
        return (rows, first, last, lastHash);
    }

    public static string FileName(string machine, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var f = TimeZoneInfo.ConvertTime(from, zone);
        var t = TimeZoneInfo.ConvertTime(to.AddSeconds(-1), zone);
        var server = new string(machine.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(40).ToArray());
        return $"zentriq-logs-{(server.Length == 0 ? "server" : server)}-{f:yyyyMMdd}-{t:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..4]}.zip";
    }

    /// <summary>Writes the archive for [from, to) into <paramref name="folder"/>.</summary>
    public static async Task<LogArchiveResult> WriteAsync(NpgsqlDataSource ds, string folder, DateTimeOffset from, DateTimeOffset to,
        bool includeAudit, bool includeActivity, string machine, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var name = FileName(machine, from, to);
        var path = Path.Combine(folder, name);
        var tmp = path + ".partial";
        long auditRows = 0, activityRows = 0;
        var manifest = new Dictionary<string, object?>
        {
            ["product"] = "Zentriq", ["createdAt"] = DateTimeOffset.UtcNow, ["server"] = machine,
            ["from"] = from.ToUniversalTime(), ["to"] = to.ToUniversalTime(), ["timeZone"] = TimeZoneInfo.Local.Id,
        };
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: true))
            {
                async Task<(long Rows, string Sha, long? First, long? Last, string? LastHash)> Part(string entry, string sql, string header, bool audit)
                {
                    // Written to a temporary file first so its checksum can go into the manifest.
                    var t = Path.Combine(folder, $".{Guid.NewGuid():N}.csv");
                    try
                    {
                        (long Rows, long? First, long? Last, string? LastHash) r;
                        await using (var f = File.Create(t)) r = await WriteCsvAsync(ds, sql, header, from, to, f, ct, audit);
                        string sha;
                        await using (var f = File.OpenRead(t)) sha = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
                        await using (var src = File.OpenRead(t))
                        await using (var dst = zip.CreateEntry(entry, CompressionLevel.Optimal).Open())
                            await src.CopyToAsync(dst, ct);
                        return (r.Rows, sha, r.First, r.Last, r.LastHash);
                    }
                    finally { try { File.Delete(t); } catch (IOException) { } }
                }
                if (includeAudit)
                {
                    var a = await Part("audit.csv", AuditSql, AuditHeader, true);
                    auditRows = a.Rows;
                    manifest["audit"] = new { file = "audit.csv", rows = a.Rows, sha256 = a.Sha, firstId = a.First, lastId = a.Last, lastHash = a.LastHash };
                }
                if (includeActivity)
                {
                    var x = await Part("activity.csv", ActivitySql, ActivityHeader, false);
                    activityRows = x.Rows;
                    manifest["activity"] = new { file = "activity.csv", rows = x.Rows, sha256 = x.Sha };
                }
                await using (var m = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
                    await m.WriteAsync(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            File.Move(tmp, path, overwrite: true);
            string fileSha;
            await using (var f = File.OpenRead(path)) fileSha = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
            return new LogArchiveResult(path, new FileInfo(path).Length, fileSha, auditRows, activityRows);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
    }
}

/// <summary>Log settings and the archive history.</summary>
public sealed class LogStore(NpgsqlDataSource ds)
{
    public async Task<LogSettings> LoadAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = @k");
        cmd.Parameters.AddWithValue("k", LogSettings.Key);
        return LogSettings.Parse(await cmd.ExecuteScalarAsync(ct) as string);
    }

    public async Task SaveAsync(LogSettings s, Guid? by, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at, updated_by) VALUES (@k, @v::jsonb, now(), @by)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now(), updated_by = EXCLUDED.updated_by
            """);
        cmd.Parameters.AddWithValue("k", LogSettings.Key);
        cmd.Parameters.AddWithValue("v", s.ToJson());
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)by ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Guid> AddAsync(string trigger, DateTimeOffset from, DateTimeOffset to, bool audit, bool activity, string status, string? by, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.log_archives (trigger, period_from, period_to, include_audit, include_activity, status, requested_by)
            VALUES (@t, @f, @to, @a, @x, @s, @by) RETURNING id
            """);
        cmd.Parameters.AddWithValue("t", trigger);
        cmd.Parameters.AddWithValue("f", from.ToUniversalTime());
        cmd.Parameters.AddWithValue("to", to.ToUniversalTime());
        cmd.Parameters.AddWithValue("a", audit);
        cmd.Parameters.AddWithValue("x", activity);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Text) { Value = (object?)by ?? DBNull.Value });
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task FinishAsync(Guid id, LogArchiveResult? r, object? copy, string? error, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.log_archives SET status = @s, file_name = @fn, size_bytes = @size, sha256 = @sha, audit_rows = @ar, activity_rows = @xr,
                   copy = @c::jsonb, error = @e, finished_at = now()
             WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", r is null ? "failed" : "succeeded");
        cmd.Parameters.Add(new NpgsqlParameter("fn", NpgsqlDbType.Text) { Value = (object?)(r is null ? null : Path.GetFileName(r.File)) ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("size", NpgsqlDbType.Bigint) { Value = (object?)r?.Size ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("sha", NpgsqlDbType.Text) { Value = (object?)r?.Sha256 ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("ar", NpgsqlDbType.Bigint) { Value = (object?)r?.AuditRows ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("xr", NpgsqlDbType.Bigint) { Value = (object?)r?.ActivityRows ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = copy is null ? DBNull.Value : JsonSerializer.Serialize(copy, LogSettings.Json) });
        cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Whether a scheduled archive of this period already succeeded.</summary>
    public async Task<bool> HasScheduledAsync(DateTimeOffset from, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT EXISTS (SELECT 1 FROM zentriq.log_archives WHERE trigger = 'schedule' AND period_from = @f AND status IN ('succeeded', 'running'))");
        cmd.Parameters.AddWithValue("f", from.ToUniversalTime());
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
