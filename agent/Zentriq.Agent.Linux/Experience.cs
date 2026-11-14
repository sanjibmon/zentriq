using System.Globalization;
using System.Text.RegularExpressions;
using Zentriq.Inventory;

namespace Zentriq.AgentLinux;

/// <summary>
/// Software metering and Device Experience on Linux. Once a minute: the programs run by people (accounts with a user
/// id of 1000 or more; path and user only), processor and memory load. At each inventory: start-up time
/// (systemd-analyze), programs that dumped core, out-of-memory kills and failed services of the last days (journal),
/// free space on the root file system and battery wear.
/// </summary>
internal static class Experience
{
    private static ExperienceRecorder? _recorder;
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(Paths.Data, "experience.json");
    private static string OffFlag => Path.Combine(Paths.Data, "metering-off");

    public static ExperienceRecorder Recorder { get { lock (Gate) return _recorder ??= new ExperienceRecorder(FilePath, p => (Path.GetFileName(p), null, null)); } }

    public static bool Metering => !File.Exists(OffFlag);

    public static void SetMetering(bool? on)
    {
        if (on is null || on.Value == Metering) return;
        try
        {
            if (on.Value) File.Delete(OffFlag);
            else { File.WriteAllText(OffFlag, "off\n"); Recorder.ClearUsage(); }
            Log.Info(on.Value ? "Software metering turned on." : "Software metering turned off; the metering totals on this device were removed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Could not change software metering: " + ex.Message); }
    }

    public static void Loop(CancellationToken ct)
    {
        var recorder = Recorder;
        var lastCpu = CpuTimes();
        try
        {
            while (!ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(60)))
            {
                try
                {
                    var now = CpuTimes();
                    double? load = null;
                    if (lastCpu is { } a && now is { } b && b.Total > a.Total) load = Math.Round(100.0 * (1 - (double)(b.Idle - a.Idle) / (b.Total - a.Total)), 1);
                    lastCpu = now;
                    var metering = Metering;
                    recorder.Sample(DateTime.Now, metering ? Running() : [], metering, load, MemoryLoad());
                    recorder.Save();
                }
                catch (Exception ex) { Log.Warn("Experience sample failed: " + ex.Message); }
            }
        }
        finally { recorder.Save(force: true); }
    }

    private static (long Idle, long Total)? CpuTimes()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
            if (line is null) return null;
            var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(x => long.TryParse(x, out var n) ? n : 0).ToArray();
            if (v.Length < 5) return null;
            return (v[3] + v[4], v.Take(8).Sum());
        }
        catch (IOException) { return null; }
    }

    private static double? MemoryLoad()
    {
        try
        {
            long total = 0, available = -1;
            foreach (var l in File.ReadLines("/proc/meminfo"))
            {
                if (l.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kb(l);
                else if (l.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = Kb(l);
            }
            return total > 0 && available >= 0 ? Math.Round(100.0 * (total - available) / total, 1) : null;
        }
        catch (IOException) { return null; }
        static long Kb(string l) => long.TryParse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out var n) ? n : 0;
    }

    private static Dictionary<int, string>? _users;
    private static DateTime _usersRead;

    private static string? UserName(int uid)
    {
        if (_users is null || DateTime.UtcNow - _usersRead > TimeSpan.FromMinutes(30))
        {
            var map = new Dictionary<int, string>();
            try
            {
                foreach (var l in File.ReadLines("/etc/passwd"))
                {
                    var f = l.Split(':');
                    if (f.Length > 3 && int.TryParse(f[2], out var id)) map[id] = f[0];
                }
            }
            catch (IOException) { }
            _users = map;
            _usersRead = DateTime.UtcNow;
        }
        return _users.TryGetValue(uid, out var n) ? n : uid.ToString(CultureInfo.InvariantCulture);
    }

    private static long? _bootTime;

    /// <summary>A process start (clock ticks after boot, field 22 of /proc/[pid]/stat) as a time, from the boot time in /proc/stat.</summary>
    private static DateTimeOffset? StartedUtc(long ticks)
    {
        if (ticks <= 0) return null;
        if (_bootTime is null)
        {
            try
            {
                foreach (var l in File.ReadLines("/proc/stat"))
                    if (l.StartsWith("btime ", StringComparison.Ordinal) && long.TryParse(l[6..].Trim(), out var b)) { _bootTime = b; break; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _bootTime ??= 0;
        }
        if (_bootTime is not > 0) return null;
        // USER_HZ is 100 on every Linux the agent supports.
        return DateTimeOffset.FromUnixTimeSeconds(_bootTime.Value).AddSeconds(ticks / 100.0);
    }

    /// <summary>Programs of people's accounts (uid 1000–60000), not of system accounts.</summary>
    private static List<RunningProgram> Running()
    {
        var list = new List<RunningProgram>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var uid = -1;
                foreach (var l in File.ReadLines(Path.Combine(dir, "status")))
                    if (l.StartsWith("Uid:", StringComparison.Ordinal)) { int.TryParse(l.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries)[1], out uid); break; }
                if (uid is < 1000 or > 60000) continue;
                var exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget;
                if (string.IsNullOrEmpty(exe)) continue;
                if (exe.EndsWith(" (deleted)", StringComparison.Ordinal)) exe = exe[..^10];
                if (!ExperienceRecorder.Metered(exe, null, null)) continue;
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var close = stat.LastIndexOf(')');
                var fields = close > 0 ? stat[(close + 2)..].Split(' ') : [];
                var started = fields.Length > 19 && long.TryParse(fields[19], out var st) ? st : 0;
                list.Add(new RunningProgram(exe, UserName(uid) ?? "", pid, started, StartedUtc(started)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return list;
    }

    public static void AddTo(InventoryDocument doc)
    {
        try
        {
            var (usage, days, since) = Recorder.Report(DateTime.Now);
            if (Metering) doc.Usage = usage;
            var report = new ExperienceReport { Since = since, Days = days };
            report.Events.AddRange(Events());
            try
            {
                var root = new DriveInfo("/");
                if (root.IsReady && root.TotalSize > 0) report.SystemFreePercent = Math.Round(100.0 * root.AvailableFreeSpace / root.TotalSize, 1);
            }
            catch (Exception) { }
            report.BatteryHealth = Battery();
            doc.Experience = report;
        }
        catch (Exception ex) { doc.Errors.Add("device experience: " + ex.Message); }
    }

    private static double? Battery()
    {
        try
        {
            foreach (var b in Directory.EnumerateDirectories("/sys/class/power_supply", "BAT*"))
            {
                double? Read(string n) => File.Exists(Path.Combine(b, n)) && double.TryParse(File.ReadAllText(Path.Combine(b, n)).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
                var full = Read("energy_full") ?? Read("charge_full");
                var design = Read("energy_full_design") ?? Read("charge_full_design");
                if (full > 0 && design > 0) return Math.Round(Math.Min(100, 100 * full.Value / design.Value), 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static readonly Regex Startup = new(@"Startup finished in (.+?) = ([^\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex Core = new(@"Process \d+ \((.+?)\) of user \d+ dumped core", RegexOptions.Compiled);
    private static readonly Regex Oom = new(@"Out of memory: Killed process \d+ \((.+?)\)", RegexOptions.Compiled);
    private static readonly Regex Failed = new(@"^(\S+?\.service): Failed with result '([^']+)'", RegexOptions.Compiled);

    internal static long? Duration(string s)
    {
        // "1min 3.2s", "7.401s", "850ms"
        double total = 0;
        foreach (Match m in Regex.Matches(s, @"([0-9.]+)(min|ms|s)"))
        {
            var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            total += m.Groups[2].Value switch { "min" => v * 60000, "ms" => v, _ => v * 1000 };
        }
        return total > 0 ? (long)total : null;
    }

    /// <summary>journalctl short-iso time: 2026-11-09T10:00:01+0000 (offset without a colon).</summary>
    internal static DateTimeOffset? Time(string s)
    {
        if (s.Length > 5 && (s[^5] == '+' || s[^5] == '-') && char.IsDigit(s[^1])) s = s[..^2] + ":" + s[^2..];
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    private static IEnumerable<ExperienceEvent> Events()
    {
        var list = new List<ExperienceEvent>();
        // Start-up of the current boot.
        var sa = Shell.Run("systemd-analyze", ["time"], TimeSpan.FromSeconds(20));
        if (sa.Code == 0 && Startup.Match(sa.Output) is { Success: true } m)
        {
            DateTimeOffset at = DateTimeOffset.Now;
            try { var up = double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture); at = DateTimeOffset.Now.AddSeconds(-up); } catch (Exception) { }
            list.Add(new ExperienceEvent { Kind = "boot", At = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, at.Offset), Ms = Duration(m.Groups[2].Value) });
        }
        void Journal(string[] args, Func<string, ExperienceEvent?> map)
        {
            var r = Shell.Run("journalctl", ["-q", "--no-pager", "-o", "short-iso", "--since=-8d", .. args], TimeSpan.FromSeconds(60));
            if (r.Code != 0) return;
            foreach (var line in r.Output.Split('\n').TakeLast(2000))
            {
                var sp = line.IndexOf(' ');
                if (sp < 10 || Time(line[..sp]) is not { } at) continue;
                var colon = line.IndexOf(": ", sp, StringComparison.Ordinal);
                if (colon < 0) continue;
                if (map(line[(colon + 2)..]) is { } e) { e.At = at; list.Add(e); }
            }
        }
        Journal(["-t", "systemd-coredump"], msg => Core.Match(msg) is { Success: true } c ? new ExperienceEvent { Kind = "crash", App = c.Groups[1].Value, Detail = "Dumped core" } : null);
        Journal(["-k"], msg => Oom.Match(msg) is { Success: true } o ? new ExperienceEvent { Kind = "oom", App = o.Groups[1].Value, Detail = "Killed: out of memory" } : null);
        Journal(["-t", "systemd", "-p", "warning"], msg => Failed.Match(msg) is { Success: true } f ? new ExperienceEvent { Kind = "service", App = f.Groups[1].Value, Detail = "Failed: " + f.Groups[2].Value } : null);
        return list.Take(1500);
    }
}
