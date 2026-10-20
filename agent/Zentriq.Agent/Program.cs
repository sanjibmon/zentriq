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
                case "--collect-now":
                {
                    var r = new AgentWorker().RunOnce(CancellationToken.None, forceInventory: cmd == "--collect-now");
                    Console.WriteLine(r.Message);
                    return r.Ok ? 0 : 1;
                }
                case "--uninstall":
                    return Uninstall(args.Length > 1 ? args[1] : null);
                case "--run":
                {
                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                    AgentService.Loop(cts.Token, initialDelay: TimeSpan.Zero);
                    return 0;
                }
                default:
                    Console.WriteLine("Zentriq Agent " + Version + "\n" +
                        "  --status             show enrollment and last check-in\n" +
                        "  --enroll <code> --server <address> [--checkin <min>] [--inventory <min>]\n" +
                        "                       enroll with an enrollment code from the console (Agents page)\n" +
                        "  --uninstall <code>   remove the agent (uninstall code from the console, valid for 1 hour)\n" +
                        "  --once               run one check-in cycle now\n" +
                        "  --collect-now        check in and send inventory now\n" +
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

    /// <summary>Removes the agent with Windows Installer, passing the uninstall code (checked by the package as SYSTEM).</summary>
    private static int Uninstall(string? code)
    {
        string? product = null;
        using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
        using (var un = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
            if (un is not null)
                foreach (var name in un.GetSubKeyNames())
                {
                    using var k = un.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") as string == "Zentriq Agent" && name.StartsWith("{")) { product = name; break; }
                }
        if (product is null) { Console.Error.WriteLine("The Zentriq Agent is not installed (no Windows Installer product found)."); return 1; }
        var arguments = $"/x {product} /passive /norestart" + (string.IsNullOrWhiteSpace(code) ? "" : $" UNINSTALLCODE={code!.Trim()}");
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), arguments) { UseShellExecute = false })!;
        p.WaitForExit();
        Console.WriteLine(p.ExitCode == 0 ? "Zentriq Agent removed." : $"Windows Installer exit code {p.ExitCode}. A missing, wrong or expired uninstall code stops the removal (see the Application event log).");
        return p.ExitCode;
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
        Console.WriteLine($"Uninstall code:  {(UninstallProtection.Enabled ? "required (get one from the Zentriq console)" : "not required")}");
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
    private static DateTime _lastWake;

    internal static void RequestWake(string why)
    {
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
        _cts = new CancellationTokenSource();
        var initial = TimeSpan.FromSeconds(new Random().Next(10, 60));
        if (!string.IsNullOrEmpty(AgentState.Load().EnrollmentCode)) initial = TimeSpan.FromSeconds(2); // just installed
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => RequestWake("Network changed");
        _thread = new Thread(() => Loop(_cts.Token, initial)) { IsBackground = true, Name = "agent" };
        _thread.Start();
    }

    protected override void OnStop()
    {
        RequestAdditionalTime(30000);
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(25));
        Log.Info("Zentriq Agent stopped.");
    }

    protected override void OnShutdown() => OnStop();

    public static void Loop(CancellationToken ct, TimeSpan initialDelay)
    {
        if (ct.WaitHandle.WaitOne(initialDelay)) return;
        var worker = new AgentWorker();
        while (!ct.IsCancellationRequested)
        {
            AgentWorker.Outcome r;
            try { r = worker.RunOnce(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Error("Unexpected error", ex); r = new AgentWorker.Outcome { NextRun = TimeSpan.FromMinutes(5) }; }
            Log.Info($"{r.Message} Next run in {(int)r.NextRun.TotalMinutes} min.");
            // Wait for the next run, or run early when the network changes / the device resumes (the network needs a
            // few seconds to settle first).
            var signalled = WaitHandle.WaitAny(new[] { ct.WaitHandle, Wake }, r.NextRun);
            if (signalled == 0) return;
            if (signalled == 1 && ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20))) return;
        }
    }
}
