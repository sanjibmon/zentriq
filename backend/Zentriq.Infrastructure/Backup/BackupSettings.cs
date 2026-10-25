using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.Infrastructure.Backup;

/// <summary>
/// When scheduled backups run (server time zone, like report schedules): daily, on chosen weekdays, or monthly on a day,
/// at a time of day. Stored in system_settings 'backup.settings'.
/// </summary>
public sealed class BackupSchedule
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("frequency")] public string Frequency { get; set; } = "daily";      // daily | weekly | monthly
    [JsonPropertyName("time")] public string Time { get; set; } = "01:30";               // HH:mm
    [JsonPropertyName("weekdays")] public int[] Weekdays { get; set; } = [0];             // 0 = Sunday … 6 = Saturday (weekly)
    [JsonPropertyName("day")] public int Day { get; set; } = 1;                           // 1–28 (monthly)

    public TimeOnly? At => TimeOnly.TryParseExact(Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    public string? Validate()
    {
        if (Frequency is not ("daily" or "weekly" or "monthly")) return "Choose daily, weekly or monthly.";
        if (At is null) return "Enter the time as HH:mm (24-hour), for example 01:30.";
        if (Frequency == "weekly" && (Weekdays.Length == 0 || Weekdays.Any(d => d is < 0 or > 6))) return "Choose at least one weekday.";
        if (Frequency == "monthly" && Day is < 1 or > 28) return "Day of the month must be 1–28.";
        return null;
    }

    private bool RunsOn(DateOnly d) => Frequency switch
    {
        "weekly" => Weekdays.Contains((int)d.DayOfWeek),
        "monthly" => d.Day == Day,
        _ => true,
    };

    private static DateTimeOffset Instant(DateOnly d, TimeOnly t, TimeZoneInfo zone)
    {
        var local = d.ToDateTime(t, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The latest scheduled time at or before <paramref name="now"/> (null when disabled or invalid).</summary>
    public DateTimeOffset? LastSlot(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        if (!Enabled || At is not { } at || Validate() is not null) return null;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var i = 0; i < 400; i++, day = day.AddDays(-1))
        {
            if (!RunsOn(day)) continue;
            var slot = Instant(day, at, zone);
            if (slot <= now) return slot;
        }
        return null;
    }

    /// <summary>The next scheduled time strictly after <paramref name="after"/>.</summary>
    public DateTimeOffset? NextRun(DateTimeOffset after, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        if (!Enabled || At is not { } at || Validate() is not null) return null;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        for (var i = 0; i < 400; i++, day = day.AddDays(1))
        {
            if (!RunsOn(day)) continue;
            var slot = Instant(day, at, zone);
            if (slot > after) return slot;
        }
        return null;
    }

    public string Describe()
    {
        if (!Enabled) return "Off";
        var names = CultureInfo.InvariantCulture.DateTimeFormat;
        return Frequency switch
        {
            "weekly" => $"Every {string.Join(", ", Weekdays.Order().Select(d => names.GetDayName((DayOfWeek)d)))} at {Time}",
            "monthly" => $"Monthly on day {Day} at {Time}",
            _ => $"Daily at {Time}",
        };
    }

    /// <summary>
    /// Whether a scheduled backup is due: the latest slot is after both the last scheduled backup and the last change of the
    /// schedule (so saving a schedule never starts a backup at once), and not older than a day (a server that was off for
    /// longer catches up once, at the next slot).
    /// </summary>
    public bool IsDue(DateTimeOffset now, DateTimeOffset? lastScheduledStart, DateTimeOffset? changedAt, TimeZoneInfo? zone = null)
    {
        if (LastSlot(now, zone) is not { } slot) return false;
        if (now - slot > TimeSpan.FromHours(24)) return false;
        if (lastScheduledStart is { } last && last >= slot) return false;
        if (changedAt is { } changed && changed >= slot) return false;
        return true;
    }
}

/// <summary>Backup options (system_settings 'backup.settings'). The password key is stored encrypted, never the password.</summary>
public sealed class BackupSettings
{
    public const string Key = "backup.settings";
    public const int MaxKeep = 1000;

    [JsonPropertyName("schedule")] public BackupSchedule Schedule { get; set; } = new();
    /// <summary>Scheduled and manual backups kept in the server's Backups folder (0 = keep all).</summary>
    [JsonPropertyName("keepLocal")] public int KeepLocal { get; set; } = 14;
    [JsonPropertyName("includeExports")] public bool IncludeExports { get; set; } = true;
    [JsonPropertyName("includeLogs")] public bool IncludeLogs { get; set; }
    [JsonPropertyName("scheduleChangedAt")] public DateTimeOffset? ScheduleChangedAt { get; set; }
    // Backup password: PBKDF2 salt and iterations (public, also in every backup header) and the derived key, encrypted with
    // the installation credential key ("zq-gcm1:…").
    [JsonPropertyName("passwordSalt")] public string? PasswordSalt { get; set; }
    [JsonPropertyName("passwordIterations")] public int? PasswordIterations { get; set; }
    [JsonPropertyName("passwordKey")] public string? PasswordKey { get; set; }
    [JsonPropertyName("passwordSetAt")] public DateTimeOffset? PasswordSetAt { get; set; }
    [JsonPropertyName("passwordSetBy")] public string? PasswordSetBy { get; set; }

    [JsonIgnore] public bool HasPassword => !string.IsNullOrEmpty(PasswordKey);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static BackupSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new BackupSettings();
        try { return JsonSerializer.Deserialize<BackupSettings>(json, Json) ?? new BackupSettings(); }
        catch (JsonException) { return new BackupSettings(); }
    }
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>What a backup holds (manifest and run history).</summary>
public sealed class BackupContents
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("databaseBytes")] public long DatabaseBytes { get; set; }
    [JsonPropertyName("certificates")] public List<string> Certificates { get; set; } = [];
    [JsonPropertyName("dataProtectionKeys")] public int DataProtectionKeys { get; set; }
    [JsonPropertyName("remoteSupportFiles")] public int RemoteSupportFiles { get; set; }
    [JsonPropertyName("reportFiles")] public int ReportFiles { get; set; }
    [JsonPropertyName("logFiles")] public int LogFiles { get; set; }

    public string Describe()
    {
        var parts = new List<string> { "database", "configuration" };
        if (Certificates.Count > 0) parts.Add("certificates");
        if (DataProtectionKeys > 0) parts.Add("MFA keys");
        if (RemoteSupportFiles > 0) parts.Add("remote support");
        if (ReportFiles > 0) parts.Add("report files");
        if (LogFiles > 0) parts.Add("logs");
        return string.Join(", ", parts);
    }
}

/// <summary>Backup file names: zentriq-{server}-{yyyyMMdd-HHmmss}-{label}.zbak (older builds: zentriq-{yyyyMMdd-HHmmss}-{label}.zbak).</summary>
public static class BackupNames
{
    /// <summary>Labels that retention may delete; backups with other labels (made on the command line) are kept.</summary>
    public static readonly string[] Rotated = ["scheduled", "manual", "nightly"];
    public static readonly string[] Safety = ["pre-upgrade", "pre-restore"];
    public const int KeepSafety = 5;

    public static string Server(string machine)
    {
        var s = new string(machine.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(40).ToArray()).Trim('-');
        return s.Length == 0 ? "server" : s;
    }

    public static string Label(string label) => new(label.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(32).ToArray());

    public static string Make(string machine, DateTime utc, string label) => $"zentriq-{Server(machine)}-{utc:yyyyMMdd-HHmmss}-{Label(label)}.zbak";

    /// <summary>Server, time and label of a backup file name; null for anything else.</summary>
    public static (string? Server, DateTime Utc, string Label)? Parse(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^zentriq-(?:(?<s>[a-z0-9-]+?)-)?(?<t>\d{8}-\d{6})-(?<l>[A-Za-z0-9-]*)\.zbak$");
        if (!m.Success || !DateTime.TryParseExact(m.Groups["t"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return null;
        return (m.Groups["s"].Success ? m.Groups["s"].Value : null, t, m.Groups["l"].Value);
    }

    /// <summary>
    /// Files retention removes: of this server's rotated backups all but the newest <paramref name="keep"/> (0 = none), and of
    /// its safety backups (pre-upgrade, pre-restore) all but the newest five each. Other servers' and hand-made backups are kept.
    /// </summary>
    public static List<string> ToDelete(IEnumerable<string> names, string machine, int keep)
    {
        var server = Server(machine);
        var mine = names.Select(n => (Name: n, P: Parse(n))).Where(x => x.P is { } p && (p.Server is null || p.Server == server)).ToList();
        var delete = new List<string>();
        if (keep > 0)
            delete.AddRange(mine.Where(x => Rotated.Contains(x.P!.Value.Label)).OrderByDescending(x => x.P!.Value.Utc).Skip(keep).Select(x => x.Name));
        foreach (var label in Safety)
            delete.AddRange(mine.Where(x => x.P!.Value.Label == label).OrderByDescending(x => x.P!.Value.Utc).Skip(KeepSafety).Select(x => x.Name));
        return delete;
    }
}
