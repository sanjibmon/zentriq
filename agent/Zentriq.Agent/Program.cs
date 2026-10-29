using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;

namespace Zentriq.Agent;

internal static class Program
{
    public const string ServiceName = "ZentriqAgent";
    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "1.0.0";
            return v.Revision > 0 ? v.ToString(4) : v.ToString(3);   // 1.1.1.31 for CI builds
        }
    }

    private static int Main(string[] args)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        if (args.Length == 0 && !Environment.UserInteractive)
        {
            ServiceBase.Run(new AgentService());
            return 0;
        }
        Log.Console = true;
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "--help";
        try
        {
            switch (cmd)
            {
                case "--version":
                    Console.WriteLine(Version);
                    return 0;
                case "--status":
                    return Status();
                case "--cleanup":
                    return Cleanup();
                case "--collect":
                {
                    // Local diagnostics: write this device's inventory to a JSON file (nothing is sent).
                    var file = args.Length > 1 ? args[1] : "zentriq-inventory.json";
                    var gz = AgentWorker.Collect(out var summary);
                    using (var input = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress))
                    using (var output = File.Create(file)) input.CopyTo(output);
                    Console.WriteLine($"{summary}\nWritten to {Path.GetFullPath(file)}");
                    return 0;
                }
                case "--enroll":
                {
                    if (args.Length < 2) { Console.Error.WriteLine("Usage: ZentriqAgent.exe --enroll <code> --server <server address> [--checkin <minutes>] [--inventory <minutes>]"); return 2; }
                    var state = AgentState.Load();
                    var server = Option(args, "--server");
                    if (Zentriq.Inventory.AgentCodes.NormalizeEnrollment(args[1]) is null && !Zentriq.Inventory.AgentProtocol.TryParseCode(args[1], out _, out _))
                    { Console.Error.WriteLine("The enrollment code must have 20 letters and digits (copy it from Agents > Enrollment codes)."); return 2; }
                    if (Zentriq.Inventory.AgentCodes.NormalizeEnrollment(args[1]) is not null && AgentWorker.NormalizeServer(server ?? state.ServerUrl) is null)
                    { Console.Error.WriteLine("Give the server address: --server https://<server>:8444"); return 2; }
                    state.EnrollmentCode = args[1].Trim().Trim('"');
                    if (server is not null) state.EnrollmentServer = AgentWorker.NormalizeServer(server);
                    if (int.TryParse(Option(args, "--checkin"), out var ci)) WriteInstallInterval("InstallCheckinMinutes", ci);
                    if (int.TryParse(Option(args, "--inventory"), out var inv)) WriteInstallInterval("InstallInventoryMinutes", inv);
                    state.Save();
                    AgentState.Secure();
                    var r = new AgentWorker().RunOnce(CancellationToken.None);
                    Console.WriteLine(r.Message);
                    return r.Ok ? 0 : 1;
                }
                case "--once":
                case "--checkin-now":
                case "--collect-now":
                    return Diagnostics.CheckinNow(inventory: cmd == "--collect-now");
                case "--diagnose":
                    return Diagnostics.Diagnose();
                case "--restart":
                    return TamperGuard.Client("restart", null);
                case "--uninstall":
                    return Uninstall(args.Length > 1 ? args[1] : null);
                case "--stop":
                case "--disable":
                case "--pause":
                    if (args.Length < 2 && UninstallProtectedFlag()) { Console.Error.WriteLine($"Usage: ZentriqAgent.exe {cmd} <code>   (a one-hour code from the Zentriq console)"); return 2; }
                    return TamperGuard.Client(cmd.Substring(2), args.Length > 1 ? args[1] : null);
                case "--enable":
                    return TamperGuard.Enable();
                case "--protection":
                    return TamperGuard.Client("status", null);
                case "--watchdog":
                    return TamperGuard.Watchdog();
                case "--uninstall-run":
                    return UninstallRun(args.Length > 1 ? args[1] : null);
                case "--run":
                {
                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                    new Thread(() => Presence.Loop(cts.Token)) { IsBackground = true, Name = "presence" }.Start();
                    AgentService.Loop(cts.Token, initialDelay: TimeSpan.Zero);
                    return 0;
                }
                default:
                    Console.WriteLine("Zentriq Agent " + Version + "\n" +
                        "  --status             show enrollment and last check-in\n" +
                        "  --enroll <code> --server <address> [--checkin <min>] [--inventory <min>]\n" +
                        "                       enroll with an enrollment code from the console (Agents page)\n" +
                        "  --uninstall <code>   remove the agent (code from the console, valid for 1 hour)\n" +
                        "  --stop <code>        stop the agent service until the device restarts (code from the console)\n" +
                        "  --disable <code>     disable and stop the agent service (code from the console)\n" +
                        "  --pause <code>       pause tamper protection for 1 hour (Services can then stop or disable it)\n" +
                        "  --enable             set the agent service to Automatic and start it\n" +
                        "  --protection         show whether tamper protection is on\n" +
                        "  --checkin-now        contact the server now (also --once); shows the result\n" +
                        "  --collect-now        contact the server now and send inventory\n" +
                        "  --diagnose           check service, enrollment, certificate, DNS, port and server key\n" +
                        "  --restart            restart the agent service (no code needed)\n" +
                        "  --collect <file>     write this device's inventory to a JSON file (nothing is sent)\n" +
                        "  --run                run in the foreground (troubleshooting)\n" +
                        "Logs: " + Log.Directory);
                    return cmd == "--help" ? 0 : 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Uninstall: delete the agent certificate/key, state and local data (run by the MSI as SYSTEM).</summary>
    private static int Cleanup()
    {
        try
        {
            var s = AgentState.Load();
            CertificateStore.Remove(s.CertThumbprint);
        }
        catch (Exception ex) { Console.Error.WriteLine("Certificate cleanup: " + ex.Message); }
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            hklm.DeleteSubKeyTree(AgentState.KeyPath, false);
        }
        catch (Exception ex) { Console.Error.WriteLine("Registry cleanup: " + ex.Message); }
        try { TamperGuard.RemoveWatchdog(); }
        catch (Exception ex) { Console.Error.WriteLine("Watchdog task cleanup: " + ex.Message); }
        try { if (Directory.Exists(AgentPaths.Queue)) Directory.Delete(AgentPaths.Queue, true); }
        catch (Exception ex) { Console.Error.WriteLine("Queue cleanup: " + ex.Message); }
        Console.WriteLine("Zentriq Agent state removed (logs kept in " + Log.Directory + ").");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static void WriteInstallInterval(string name, int minutes)
    {
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var k = hklm.CreateSubKey(AgentState.KeyPath, true);
        k.SetValue(name, minutes, Microsoft.Win32.RegistryValueKind.DWord);
    }

    /// <summary>The log of the most recent removal started with --uninstall.</summary>
    public static string UninstallLogPath => Path.Combine(Path.GetTempPath(), "ZentriqAgent-uninstall.log");

    /// <summary>
    /// Removes the agent with Windows Installer, passing the uninstall code (checked by the package as SYSTEM).
    /// This program runs from the folder being removed, and Windows Installer closes programs that use files it removes,
    /// so the removal runs from a temporary copy and this process exits at once.
    /// </summary>
    private static int Uninstall(string? code)
    {
        if (FindProduct() is null) { Console.Error.WriteLine("The Zentriq Agent is not installed (no Windows Installer product found)."); return 1; }
        var source = AppDomain.CurrentDomain.BaseDirectory;
        var temp = Path.Combine(Path.GetTempPath(), "ZentriqAgentRemove-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(temp);
        foreach (var f in Directory.GetFiles(source))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext == ".exe" || ext == ".dll" || ext == ".config") File.Copy(f, Path.Combine(temp, Path.GetFileName(f)), true);
        }
        var exe = Path.Combine(temp, Path.GetFileName(Process.GetCurrentProcess().MainModule!.FileName));
        var arguments = "--uninstall-run" + (string.IsNullOrWhiteSpace(code) ? "" : " \"" + code!.Trim().Trim('"') + "\"");
        Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = temp });
        Console.WriteLine("Removing the Zentriq Agent (Windows Installer shows the progress).");
        Console.WriteLine("Result and details: " + UninstallLogPath);
        return 0;
    }

    /// <summary>The removal itself, run from the temporary copy made by <see cref="Uninstall"/>.</summary>
    private static int UninstallRun(string? code)
    {
        var product = FindProduct();
        int exit;
        if (product is null) { Console.Error.WriteLine("The Zentriq Agent is not installed."); exit = 1; }
        else
        {
            var arguments = $"/x {product} /passive /norestart /l*v \"{UninstallLogPath}\"" + (string.IsNullOrWhiteSpace(code) ? "" : $" UNINSTALLCODE={code!.Trim()}");
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), arguments) { UseShellExecute = false })!;
            p.WaitForExit();
            exit = p.ExitCode;
            Console.WriteLine(exit == 0 || exit == 3010 ? "Zentriq Agent removed." : $"Windows Installer exit code {exit}. A missing, wrong or expired uninstall code stops the removal (details: {UninstallLogPath}).");
        }
        // Remove this temporary copy once this process has ended.
        try
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (Path.GetFileName(dir).StartsWith("ZentriqAgentRemove-", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c ping -n 4 127.0.0.1 >nul & rd /s /q \"{dir}\"")
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
        }
        catch { }
        return exit;
    }

    private static string? FindProduct()
    {
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var un = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (un is null) return null;
        foreach (var name in un.GetSubKeyNames())
        {
            using var k = un.OpenSubKey(name);
            if (k?.GetValue("DisplayName") as string == "Zentriq Agent" && name.StartsWith("{")) return name;
        }
        return null;
    }

    /// <summary>Whether protection is on (readable by administrators; the key itself is SYSTEM only).</summary>
    private static bool UninstallProtectedFlag()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var k = hklm.OpenSubKey(AgentState.KeyPath, false);
            return k?.GetValue("UninstallProtected") is int i && i == 1;
        }
        catch (Exception) { return false; }
    }

    private static int Status()
    {
        var s = AgentState.Load();
        Console.WriteLine($"Zentriq Agent {Version}");
        Console.WriteLine($"Status:          {s.Status}");
        Console.WriteLine($"Server:          {s.ServerUrl ?? "-"}");
        Console.WriteLine($"Agent ID:        {s.AgentId ?? "-"}");
        Console.WriteLine($"Certificate:     {s.CertThumbprint ?? "-"}");
        Console.WriteLine($"Last check-in:   {s.LastCheckinUtc?.ToString("u") ?? "never"}");
        Console.WriteLine($"Last inventory:  {s.LastInventoryUtc?.ToString("u") ?? "never"}");
        Console.WriteLine($"Check-in every:  {s.CheckinMinutes} min");
        Console.WriteLine($"Inventory every: {s.InventoryMinutes} min");
        Console.WriteLine($"Other addresses: {(s.ServerUrls.Count == 0 ? "-" : string.Join(", ", s.ServerUrls))}");
        if (s.OfflineSinceUtc is { } off) Console.WriteLine($"Offline since:   {off:u} (the server has not answered since then)");
        Console.WriteLine($"Uninstall code:  {(UninstallProtectedFlag() ? "required (get one from the Zentriq console)" : "not required")}");
        Console.WriteLine($"Stop / disable:  {(UninstallProtectedFlag() ? "need a code from the Zentriq console (--stop / --disable <code>)" : "allowed")}");
        Console.WriteLine($"Pending code:    {(string.IsNullOrEmpty(s.EnrollmentCode) ? "no" : "yes")}");
        if (!string.IsNullOrEmpty(s.LastError)) Console.WriteLine($"Last error:      {s.LastError}");
        return s.Enrolled ? 0 : 1;
    }
}

internal sealed class AgentService : ServiceBase
{
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    public AgentService()
    {
        ServiceName = Program.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandlePowerEvent = true;
        AutoLog = true;
    }

    /// <summary>Set to run the next cycle early: the network changed or the device woke from sleep.</summary>
    internal static readonly AutoResetEvent Wake = new AutoResetEvent(false);
    /// <summary>Set by the live connection: the server has work for this agent, or the connection came back after a gap.</summary>
    internal static readonly AutoResetEvent ServerWake = new AutoResetEvent(false);
    private Thread? _presence, _guard, _pipe;

    internal static void RequestServerWake() => ServerWake.Set();

    private static int _forceInventory;
    private static readonly object OutcomeGate = new object();
    private static DateTime _outcomeAt;
    private static AgentWorker.Outcome? _outcome;

    /// <summary>Runs a check-in cycle now (asked for on the device: --checkin-now) and waits for its result.</summary>
    internal static AgentWorker.Outcome? RunNow(bool inventory, TimeSpan timeout)
    {
        var since = DateTime.UtcNow;
        if (inventory) Interlocked.Exchange(ref _forceInventory, 1);
        Presence.Retry.Set();
        ServerWake.Set();
        var until = since + timeout;
        lock (OutcomeGate)
        {
            while (_outcomeAt <= since)
            {
                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return null;
                Monitor.Wait(OutcomeGate, left);
            }
            return _outcome;
        }
    }

    private static void SetOutcome(AgentWorker.Outcome r)
    {
        lock (OutcomeGate) { _outcome = r; _outcomeAt = DateTime.UtcNow; Monitor.PulseAll(OutcomeGate); }
    }
    private static DateTime _lastWake;

    internal static void RequestWake(string why)
    {
        Presence.Retry.Set();   // retry a lost live connection at once
        if (DateTime.UtcNow - _lastWake < TimeSpan.FromSeconds(30)) return;
        _lastWake = DateTime.UtcNow;
        Log.Info($"{why}: checking the connection to the server.");
        Wake.Set();
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus == PowerBroadcastStatus.ResumeSuspend || powerStatus == PowerBroadcastStatus.ResumeAutomatic) RequestWake("Resumed from sleep");
        return true;
    }

    protected override void OnStart(string[] args)
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
        AgentState.Secure();
        Log.Info($"Zentriq Agent {Program.Version} starting.");
        TamperGuard.OnServiceStart();
        _cts = new CancellationTokenSource();
        var initial = TimeSpan.FromSeconds(new Random().Next(10, 60));
        if (!string.IsNullOrEmpty(AgentState.Load().EnrollmentCode)) initial = TimeSpan.FromSeconds(2); // just installed
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => RequestWake("Network changed");
        _thread = new Thread(() => Loop(_cts.Token, initial)) { IsBackground = true, Name = "agent" };
        _thread.Start();
        // Live connection from the start: the console shows the device online within seconds.
        var token = _cts.Token;
        _presence = new Thread(() => { try { Presence.Loop(token); } catch (Exception ex) { Log.Error("Live connection stopped", ex); } }) { IsBackground = true, Name = "presence" };
        _presence.Start();
        // Tamper protection: periodic check and the local pipe for --stop / --disable / --pause with a code.
        _guard = new Thread(() => { try { TamperGuard.Loop(token); } catch (Exception ex) { Log.Error("Tamper protection check stopped", ex); } }) { IsBackground = true, Name = "guard" };
        _guard.Start();
        _pipe = new Thread(() => { try { TamperGuard.PipeLoop(token); } catch (Exception ex) { Log.Error("Local control pipe stopped", ex); } }) { IsBackground = true, Name = "pipe" };
        _pipe.Start();
    }

    protected override void OnStop()
    {
        RequestAdditionalTime(30000);
        _cts?.Cancel();
        _presence?.Join(TimeSpan.FromSeconds(3));   // closing the live connection shows the device offline at once
        _thread?.Join(TimeSpan.FromSeconds(25));
        Log.Info("Zentriq Agent stopped.");
    }

    protected override void OnShutdown() => OnStop();

    public static void Loop(CancellationToken ct, TimeSpan initialDelay)
    {
        // Start after a short random delay, or at once when the server has work (live connection) or the network changes.
        if (WaitHandle.WaitAny(new[] { ct.WaitHandle, ServerWake, Wake }, initialDelay) == 0) return;
        var worker = new AgentWorker();
        var lastRun = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            AgentWorker.Outcome r;
            try { r = worker.RunOnce(ct, forceInventory: Interlocked.Exchange(ref _forceInventory, 0) == 1); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Error("Unexpected error", ex); r = new AgentWorker.Outcome { NextRun = TimeSpan.FromMinutes(5) }; }
            Log.Info($"{r.Message} Next run in {(int)r.NextRun.TotalMinutes} min.");
            SetOutcome(r);
            // Wait for the next run, or run early when the network changes / the device resumes (the network needs a
            // few seconds to settle first).
            // Work queued by the server runs at once (at most every 10 seconds).
            var signalled = WaitHandle.WaitAny(new[] { ct.WaitHandle, Wake, ServerWake }, r.NextRun);
            if (signalled == 0) return;
            if (signalled == 1 && ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20))) return;
            if (signalled == 2)
            {
                var since = DateTime.UtcNow - lastRun;
                if (since < TimeSpan.FromSeconds(10) && ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10) - since)) return;
            }
            lastRun = DateTime.UtcNow;
        }
    }
}
