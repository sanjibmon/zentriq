using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>
/// Software metering and Device Experience on Windows. Once a minute: the programs running in signed-in users'
/// sessions (full path and user only — never window titles, documents or keystrokes), processor and memory load.
/// At each inventory: boot and sign-in times, application crashes and hangs, blue screens, unexpected shutdowns and
/// service failures of the last days from the event logs, free space on the system drive and battery wear.
/// </summary>
internal static class Experience
{
    private static ExperienceRecorder? _recorder;
    private static readonly object Gate = new object();

    public static string File => Path.Combine(AgentPaths.Root, "experience.json");

    public static ExperienceRecorder Recorder
    {
        get { lock (Gate) return _recorder ??= new ExperienceRecorder(File, Describe); }
    }

    /// <summary>Software metering on or off (from the server's Software License Management settings).</summary>
    public static bool Metering
    {
        get
        {
            try { using var k = Registry.LocalMachine.OpenSubKey(AgentState.KeyPath); return !(k?.GetValue("Metering") is int v && v == 0); }
            catch (Exception) { return true; }
        }
    }

    public static void SetMetering(bool? on)
    {
        if (on is null || on.Value == Metering) return;
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(AgentState.KeyPath);
            k?.SetValue("Metering", on.Value ? 1 : 0, RegistryValueKind.DWord);
            Log.Info(on.Value ? "Software metering turned on." : "Software metering turned off; the metering totals on this device were removed.");
            if (!on.Value) Recorder.ClearUsage();
        }
        catch (Exception ex) { Log.Warn("Could not change software metering: " + ex.Message); }
    }

    public static void Loop(CancellationToken ct)
    {
        var recorder = Recorder;
        PerformanceCounter? cpu = null;
        try { cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", true); cpu.NextValue(); }
        catch (Exception ex) { Log.Warn("Processor load is not available (performance counters): " + ex.Message); cpu = null; }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var agentDir = AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            while (!ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(60)))
            {
                try
                {
                    double? load = null;
                    try { if (cpu is not null) load = cpu.NextValue(); } catch (Exception) { load = null; }
                    var metering = Metering;
                    var programs = metering ? Running(windows, agentDir) : new List<RunningProgram>();
                    recorder.Sample(DateTime.Now, programs, metering, load, MemoryLoad());
                    recorder.Save();
                }
                catch (Exception ex) { Log.Warn("Experience sample failed: " + ex.Message); }
            }
        }
        finally
        {
            recorder.Save(force: true);
            cpu?.Dispose();
        }
    }

    /// <summary>The programs in users' sessions (session 0 is services and is not metered).</summary>
    private static List<RunningProgram> Running(string windows, string agentDir)
    {
        var list = new List<RunningProgram>();
        var users = new Dictionary<int, string?>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var session = p.SessionId;
                    if (session == 0) continue;
                    var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        var size = 1024;
                        var sb = new StringBuilder(size);
                        if (!QueryFullProcessImageName(h, 0, sb, ref size)) continue;
                        var path = sb.ToString();
                        if (!ExperienceRecorder.Metered(path, windows, agentDir)) continue;
                        long started = 0;
                        if (GetProcessTimes(h, out var c, out _, out _, out _)) started = c;
                        if (!users.TryGetValue(session, out var user)) users[session] = user = InstallerWatch.SessionUser(session);
                        if (user is null) continue;
                        list.Add(new RunningProgram(path, user, p.Id, started, started > 0 ? new DateTimeOffset(DateTime.FromFileTimeUtc(started)) : (DateTimeOffset?)null));
                    }
                    finally { CloseHandle(h); }
                }
                catch (Exception) { }
            }
        }
        return list;
    }

    private static (string? Product, string? Publisher, string? Version) Describe(string path)
    {
        var v = FileVersionInfo.GetVersionInfo(path);
        static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim().Length > 200 ? s.Trim().Substring(0, 200) : s.Trim();
        return (T(v.ProductName) ?? T(v.FileDescription), T(v.CompanyName), T(v.ProductVersion) ?? T(v.FileVersion));
    }

    private static double? MemoryLoad()
    {
        var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
        return GlobalMemoryStatusEx(ref m) ? m.MemoryLoad : (double?)null;
    }

    /// <summary>Adds metering and Device Experience to an inventory document.</summary>
    public static void AddTo(InventoryDocument doc)
    {
        try
        {
            var (usage, days, since) = Recorder.Report(DateTime.Now);
            if (Metering) doc.Usage = usage;
            var report = new ExperienceReport { Since = since, Days = days };
            report.Events.AddRange(Events(DateTime.Now.AddDays(-ExperienceRecorder.ReportDays)));
            try
            {
                var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
                if (sys.IsReady && sys.TotalSize > 0) report.SystemFreePercent = Math.Round(100.0 * sys.AvailableFreeSpace / sys.TotalSize, 1);
            }
            catch (Exception) { }
            report.BatteryHealth = BatteryHealth();
            doc.Experience = report;
        }
        catch (Exception ex) { doc.Errors.Add("device experience: " + ex.Message); }
    }

    private static double? BatteryHealth()
    {
        try
        {
            var wmi = new LocalWmiSource();
            var full = wmi.Query(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity").FirstOrDefault();
            var design = wmi.Query(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData").FirstOrDefault();
            if (full is null || design is null) return null;
            var f = Convert.ToDouble(full["FullChargedCapacity"]);
            var d = Convert.ToDouble(design["DesignedCapacity"]);
            return d > 0 && f > 0 ? Math.Round(Math.Min(100, 100 * f / d), 1) : (double?)null;
        }
        catch (Exception) { return null; }
    }

    private static IEnumerable<ExperienceEvent> Events(DateTime since)
    {
        var ms = (long)Math.Max(1, (DateTime.Now - since).TotalMilliseconds);
        var list = new List<ExperienceEvent>();
        string Q(string providers, string ids) => $"*[System[({providers}) and ({ids}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
        string P(params string[] names) => string.Join(" or ", names.Select(n => $"Provider[@Name='{n}']"));
        string I(params int[] ids) => string.Join(" or ", ids.Select(i => $"EventID={i}"));

        Read("Microsoft-Windows-Diagnostics-Performance/Operational", Q(P("Microsoft-Windows-Diagnostics-Performance"), I(100)), list, e =>
            new ExperienceEvent { Kind = "boot", Ms = Long(e, "MainPathBootTime"), Ms2 = Long(e, "BootPostBootTime") });
        Read("Application", Q(P("Application Error"), I(1000)), list, e =>
            new ExperienceEvent { Kind = "crash", App = Prop(e, 0), Version = Prop(e, 1), Detail = Prop(e, 3) is { } m ? "Faulting module " + m + (Prop(e, 6) is { } code ? ", exception " + code : "") : null });
        Read("Application", Q(P("Application Hang"), I(1002)), list, e =>
            new ExperienceEvent { Kind = "hang", App = Prop(e, 0), Version = Prop(e, 1), Detail = "Stopped responding and was closed" });
        Read("System", Q(P("Microsoft-Windows-WER-SystemErrorReporting"), I(1001)), list, e =>
            new ExperienceEvent { Kind = "bugcheck", Detail = Prop(e, 0) is { } code ? "Stop code " + code : "Blue screen" });
        Read("System", Q(P("Microsoft-Windows-Kernel-Power"), I(41)), list, e =>
            new ExperienceEvent { Kind = "shutdown", Detail = "The device restarted without shutting down cleanly (power loss, hang or blue screen)" });
        Read("System", Q(P("Service Control Manager"), I(7031, 7034)), list, e =>
            new ExperienceEvent { Kind = "service", App = Prop(e, 0), Detail = "Service terminated unexpectedly" });
        return list;
    }

    private static void Read(string log, string query, List<ExperienceEvent> into, Func<EventRecord, ExperienceEvent> map)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = true });
            var n = 0;
            for (var e = reader.ReadEvent(); e is not null && n < 200; e = reader.ReadEvent(), n++)
                using (e)
                {
                    try
                    {
                        var x = map(e);
                        x.At = e.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.Now;
                        if (x.App is { Length: > 200 }) x.App = x.App.Substring(0, 200);
                        if (x.Detail is { Length: > 400 }) x.Detail = x.Detail.Substring(0, 400);
                        into.Add(x);
                    }
                    catch (Exception) { }
                }
        }
        catch (EventLogNotFoundException) { }
        catch (Exception ex) { Log.Warn($"Device experience: event log {log} could not be read: {ex.Message}"); }
    }

    private static string? Prop(EventRecord e, int i) =>
        e.Properties is { } p && i < p.Count && p[i]?.Value is { } v && v.ToString() is { Length: > 0 } s ? s.Trim() : null;

    /// <summary>A named value of the event's data (Diagnostics-Performance events have many; their order varies by Windows version).</summary>
    private static long? Long(EventRecord e, string name)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(e.ToXml(), "<Data Name=['\"]" + name + "['\"]>(\\d+)</Data>");
            return m.Success && long.TryParse(m.Groups[1].Value, out var v) && v > 0 ? v : (long?)null;
        }
        catch (Exception) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
}
