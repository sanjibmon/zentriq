using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.Inventory;

/// <summary>Software metering: which programs ran on the device, for whom, how long, per day.</summary>
public sealed class UsageReport
{
    /// <summary>First day the agent metered (yyyy-MM-dd): a program without use since then was not used.</summary>
    [JsonPropertyName("since")] public string Since { get; set; } = "";
    [JsonPropertyName("items")] public List<UsageItem> Items { get; set; } = new();
    /// <summary>Each run of a program (build 142): started, closed (null while running).</summary>
    [JsonPropertyName("sessions")] public List<UsageSession> Sessions { get; set; } = new();
}

/// <summary>
/// One run of a program for one user: from the first sample (or the process start time) with the program running to
/// the last sample with it running. Several windows or processes of the program are one run.
/// </summary>
public sealed class UsageSession
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("exe")] public string Exe { get; set; } = "";
    [JsonPropertyName("product")] public string? Product { get; set; }
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("start")] public DateTimeOffset Start { get; set; }
    /// <summary>Null while the program is still running.</summary>
    [JsonPropertyName("end")] public DateTimeOffset? End { get; set; }
    [JsonPropertyName("lastSeen")] public DateTimeOffset LastSeen { get; set; }
}

public sealed class UsageItem
{
    [JsonPropertyName("day")] public string Day { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("exe")] public string Exe { get; set; } = "";
    [JsonPropertyName("product")] public string? Product { get; set; }
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("user")] public string User { get; set; } = "";
    /// <summary>Minutes the program was running (sampled once a minute).</summary>
    [JsonPropertyName("minutes")] public int Minutes { get; set; }
    [JsonPropertyName("launches")] public int Launches { get; set; }
}

/// <summary>Device Experience: resource pressure per day and the events that make a device slow or unreliable.</summary>
public sealed class ExperienceReport
{
    [JsonPropertyName("since")] public string Since { get; set; } = "";
    [JsonPropertyName("days")] public List<ExperienceDay> Days { get; set; } = new();
    [JsonPropertyName("events")] public List<ExperienceEvent> Events { get; set; } = new();
    /// <summary>Free space on the system drive (percent) when the report was made.</summary>
    [JsonPropertyName("systemFreePercent")] public double? SystemFreePercent { get; set; }
    /// <summary>Battery: full charge capacity as a percentage of the design capacity.</summary>
    [JsonPropertyName("batteryHealth")] public double? BatteryHealth { get; set; }
}

public sealed class ExperienceDay
{
    [JsonPropertyName("day")] public string Day { get; set; } = "";
    [JsonPropertyName("samples")] public int Samples { get; set; }
    [JsonPropertyName("cpuAvg")] public double CpuAvg { get; set; }
    /// <summary>Minutes with the processor at 90 % or more.</summary>
    [JsonPropertyName("cpuHigh")] public int CpuHigh { get; set; }
    [JsonPropertyName("memAvg")] public double MemAvg { get; set; }
    /// <summary>Minutes with memory 90 % or more in use.</summary>
    [JsonPropertyName("memHigh")] public int MemHigh { get; set; }
}

public sealed class ExperienceEvent
{
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    /// <summary>boot | crash | hang | bugcheck | shutdown | service | oom</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("app")] public string? App { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    /// <summary>boot: time to the sign-in screen; logon: from sign-in to a usable desktop (milliseconds).</summary>
    [JsonPropertyName("ms")] public long? Ms { get; set; }
    [JsonPropertyName("ms2")] public long? Ms2 { get; set; }
}

/// <summary>A running program seen by a sample: full path, the user of its session, its process id and start time.</summary>
public readonly struct RunningProgram
{
    public RunningProgram(string path, string user, int pid, long started, DateTimeOffset? startedUtc = null) { Path = path; User = user; Pid = pid; Started = started; StartedUtc = startedUtc; }
    public string Path { get; }
    public string User { get; }
    public int Pid { get; }
    public long Started { get; }
    /// <summary>When the process started, when known (gives the run its real start time).</summary>
    public DateTimeOffset? StartedUtc { get; }
}

/// <summary>
/// Collects metering and resource samples once a minute and keeps daily totals on disk (35 days), shared by the
/// Windows and Linux agents. Only totals per program, user and day are kept: never window titles, documents or
/// keystrokes.
/// </summary>
public sealed class ExperienceRecorder
{
    public const int KeepDays = 35;
    public const int ReportDays = 8;
    public const double High = 90;

    private sealed class Store
    {
        [JsonPropertyName("since")] public string Since { get; set; } = "";
        [JsonPropertyName("usage")] public List<UsageItem> Usage { get; set; } = new();
        [JsonPropertyName("sessions")] public List<UsageSession> Sessions { get; set; } = new();
        [JsonPropertyName("days")] public List<ExperienceDay> Days { get; set; } = new();
        [JsonPropertyName("cpuSum")] public Dictionary<string, double> CpuSum { get; set; } = new();
        [JsonPropertyName("memSum")] public Dictionary<string, double> MemSum { get; set; } = new();
    }

    private readonly string _file;
    private readonly Func<string, (string? Product, string? Publisher, string? Version)> _describe;
    private readonly Dictionary<string, (string? Product, string? Publisher, string? Version)> _described = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new object();
    private Store _store;
    private HashSet<string>? _seen;   // pid:start of the programs already counted as launched
    private int _dirty;

    public ExperienceRecorder(string file, Func<string, (string? Product, string? Publisher, string? Version)> describe)
    {
        _file = file;
        _describe = describe;
        _store = Load(file);
    }

    private static Store Load(string file)
    {
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<Store>(File.ReadAllText(file)) ?? new Store();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new Store();
    }

    public static string Day(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>One sample (normally once a minute). The first sample after start counts nothing as launched.</summary>
    public void Sample(DateTime local, IEnumerable<RunningProgram> programs, bool metering, double? cpu, double? mem)
    {
        var day = Day(local);
        lock (_gate)
        {
            if (_store.Since.Length == 0) _store.Since = day;
            if (metering)
            {
                var first = _seen is null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in programs)
                {
                    if (string.IsNullOrEmpty(p.Path)) continue;
                    var user = string.IsNullOrEmpty(p.User) ? "" : p.User;
                    var id = p.Pid.ToString(CultureInfo.InvariantCulture) + ":" + p.Started.ToString(CultureInfo.InvariantCulture);
                    seen.Add(id);
                    var key = p.Path + "|" + user;
                    var item = _store.Usage.FirstOrDefault(u => u.Day == day && string.Equals(u.Path, p.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(u.User, user, StringComparison.OrdinalIgnoreCase));
                    if (item is null)
                    {
                        if (!_described.TryGetValue(p.Path, out var d))
                        {
                            try { d = _describe(p.Path); } catch (Exception) { d = (null, null, null); }
                            _described[p.Path] = d;
                        }
                        item = new UsageItem { Day = day, Path = p.Path, Exe = FileName(p.Path), Product = d.Product, Publisher = d.Publisher, Version = d.Version, User = user };
                        _store.Usage.Add(item);
                    }
                    if (counted.Add(key)) item.Minutes++;           // several windows / processes of one program: one minute
                    if (!first && !_seen!.Contains(id)) item.Launches++;
                }
                _seen = seen;
                TrackSessions(local, programs);
            }
            if (cpu is not null || mem is not null)
            {
                var d = _store.Days.FirstOrDefault(x => x.Day == day);
                if (d is null) { d = new ExperienceDay { Day = day }; _store.Days.Add(d); }
                d.Samples++;
                var c = Math.Max(0, Math.Min(100, cpu ?? 0));
                var m = Math.Max(0, Math.Min(100, mem ?? 0));
                _store.CpuSum[day] = (_store.CpuSum.TryGetValue(day, out var cs) ? cs : 0) + c;
                _store.MemSum[day] = (_store.MemSum.TryGetValue(day, out var ms) ? ms : 0) + m;
                d.CpuAvg = Math.Round(_store.CpuSum[day] / d.Samples, 1);
                d.MemAvg = Math.Round(_store.MemSum[day] / d.Samples, 1);
                if (c >= High) d.CpuHigh++;
                if (m >= High) d.MemHigh++;
            }
            Prune(local);
            _dirty++;
        }
    }

    /// <summary>A run continues while its program is seen at least every few minutes (a missed sample is not a close).</summary>
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(3);

    private void TrackSessions(DateTime local, IEnumerable<RunningProgram> programs)
    {
        var now = new DateTimeOffset(local.Kind == DateTimeKind.Utc ? local : local.ToUniversalTime(), TimeSpan.Zero);
        // Programs running now, per program and user, with the earliest known process start.
        var running = new Dictionary<string, (RunningProgram P, DateTimeOffset? Start)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in programs)
        {
            if (string.IsNullOrEmpty(p.Path)) continue;
            var key = p.Path + "|" + (p.User ?? "");
            var st = p.StartedUtc is { } s && s <= now.AddMinutes(1) && s > now.AddDays(-30) ? s : (DateTimeOffset?)null;
            if (!running.TryGetValue(key, out var r)) running[key] = (p, st);
            else if (st is { } s2 && (r.Start is null || s2 < r.Start)) running[key] = (r.P, s2);
        }
        var continued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in _store.Sessions.Where(x => x.End is null))
        {
            var key = x.Path + "|" + x.User;
            // Still running: seen recently, or the same process instance as before (the agent was stopped meanwhile).
            if (running.TryGetValue(key, out var r) && (now - x.LastSeen <= SessionGap || (r.Start is { } st && st <= x.LastSeen)))
            {
                x.LastSeen = now;
                continued.Add(key);
            }
            else x.End = x.LastSeen;
        }
        foreach (var kv in running)
        {
            if (continued.Contains(kv.Key)) continue;
            var p = kv.Value.P;
            var start = kv.Value.Start ?? now;
            // Not before the end of the previous run of the same program and user.
            var prev = _store.Sessions.Where(x => x.End is not null && string.Equals(x.Path, p.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(x.User, p.User ?? "", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.End!.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
            if (start < prev) start = prev;
            _described.TryGetValue(p.Path, out var d);
            _store.Sessions.Add(new UsageSession { Path = p.Path, Exe = FileName(p.Path), Product = d.Product, User = p.User ?? "", Start = start, LastSeen = now });
        }
    }

    private void Prune(DateTime local)
    {
        var oldest = Day(local.AddDays(-KeepDays));
        _store.Usage.RemoveAll(u => string.CompareOrdinal(u.Day, oldest) < 0);
        _store.Days.RemoveAll(u => string.CompareOrdinal(u.Day, oldest) < 0);
        foreach (var k in _store.CpuSum.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList()) { _store.CpuSum.Remove(k); _store.MemSum.Remove(k); }
        var oldestRun = new DateTimeOffset(local.AddDays(-KeepDays).ToUniversalTime());
        _store.Sessions.RemoveAll(x => x.End is { } e && e < oldestRun);
        if (_store.Sessions.Count > 5000)
            _store.Sessions = _store.Sessions.OrderByDescending(x => x.LastSeen).Take(4000).ToList();
        // A device that runs thousands of different programs: keep the most used.
        if (_store.Usage.Count > 5000)
            _store.Usage = _store.Usage.OrderByDescending(u => u.Day).ThenByDescending(u => u.Minutes).Take(4000).ToList();
    }

    /// <summary>Writes the totals to disk when something changed (every few samples and when the service stops).</summary>
    public void Save(bool force = false)
    {
        lock (_gate)
        {
            if (_dirty == 0 || (!force && _dirty < 10)) return;
            try
            {
                var dir = Path.GetDirectoryName(_file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_store));
                if (File.Exists(_file)) File.Delete(_file);
                File.Move(tmp, _file);
                _dirty = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Forgets the metering totals (metering turned off in the console).</summary>
    public void ClearUsage()
    {
        lock (_gate) { if (_store.Usage.Count == 0 && _store.Sessions.Count == 0 && _seen is null) return; _store.Usage.Clear(); _store.Sessions.Clear(); _seen = null; _store.Since = ""; _dirty++; }
        Save(force: true);
    }

    /// <summary>The last <see cref="ReportDays"/> days, sent with every inventory (the server replaces what it had for those days).</summary>
    public (UsageReport Usage, List<ExperienceDay> Days, string Since) Report(DateTime local)
    {
        var from = Day(local.AddDays(-(ReportDays - 1)));
        lock (_gate)
        {
            var usage = new UsageReport
            {
                Since = _store.Since,
                Items = _store.Usage.Where(u => string.CompareOrdinal(u.Day, from) >= 0).Select(Copy).ToList(),
            };
            var fromRun = new DateTimeOffset(local.AddDays(-ReportDays).ToUniversalTime());
            usage.Sessions = _store.Sessions.Where(x => x.End is null || x.End >= fromRun)
                .Select(x => new UsageSession { Path = x.Path, Exe = x.Exe, Product = x.Product, User = x.User, Start = x.Start, End = x.End, LastSeen = x.LastSeen }).ToList();
            var days = _store.Days.Where(d => string.CompareOrdinal(d.Day, from) >= 0)
                .Select(d => new ExperienceDay { Day = d.Day, Samples = d.Samples, CpuAvg = d.CpuAvg, CpuHigh = d.CpuHigh, MemAvg = d.MemAvg, MemHigh = d.MemHigh }).ToList();
            return (usage, days, _store.Since);
        }
    }

    private static UsageItem Copy(UsageItem u) => new UsageItem
    {
        Day = u.Day, Path = u.Path, Exe = u.Exe, Product = u.Product, Publisher = u.Publisher, Version = u.Version, User = u.User, Minutes = u.Minutes, Launches = u.Launches,
    };

    /// <summary>The file name of a Windows or Linux path, whatever system this runs on.</summary>
    public static string FileName(string path) => path.Substring(path.LastIndexOfAny(new[] { '\\', '/' }) + 1);

    /// <summary>Programs that are part of the operating system or of Zentriq are not metered.</summary>
    public static bool Metered(string path, string? windowsDir, string? agentDir)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (!string.IsNullOrEmpty(windowsDir) && path.StartsWith(windowsDir!.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(agentDir) && path.StartsWith(agentDir!.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith("/")) return !LinuxSystemPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal)) && !LinuxSystemNames.Contains(FileName(path));
        return true;
    }

    private static readonly string[] LinuxSystemPrefixes = { "/usr/lib/systemd/", "/lib/systemd/", "/usr/libexec/", "/usr/sbin/", "/sbin/", "/opt/zentriq/", "/proc/", "/snap/core" };
    private static readonly HashSet<string> LinuxSystemNames = new(StringComparer.Ordinal)
    {
        "bash", "sh", "dash", "zsh", "sudo", "su", "login", "sshd", "dbus-daemon", "dbus-broker", "systemd", "agetty", "cron", "crond", "tmux", "screen",
        "Xorg", "Xwayland", "gnome-shell", "gnome-session-binary", "plasmashell", "kwin_x11", "kwin_wayland", "pipewire", "pulseaudio", "wireplumber", "at-spi-bus-launcher",
    };
}
