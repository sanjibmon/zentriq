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
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

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
                    if (args.Length < 2) { Console.Error.WriteLine("Usage: ZentriqAgent.exe --enroll <ZQ1.code>"); return 2; }
                    if (!Zentriq.Inventory.AgentProtocol.TryParseCode(args[1], out _, out var err)) { Console.Error.WriteLine(err); return 2; }
                    var state = AgentState.Load();
                    state.EnrollmentCode = args[1].Trim().Trim('"');
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
                        "  --enroll <code>      enroll with a ZQ1. code from the console (Agents page)\n" +
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
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
        AgentState.Secure();
        Log.Info($"Zentriq Agent {Program.Version} starting.");
        _cts = new CancellationTokenSource();
        var initial = TimeSpan.FromSeconds(new Random().Next(10, 60));
        if (!string.IsNullOrEmpty(AgentState.Load().EnrollmentCode)) initial = TimeSpan.FromSeconds(2); // just installed
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
            if (ct.WaitHandle.WaitOne(r.NextRun)) return;
        }
    }
}
