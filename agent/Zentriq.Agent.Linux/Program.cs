using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using Zentriq.Inventory;

namespace Zentriq.AgentLinux;

internal static class Program
{
    private static int Main(string[] args)
    {
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "--help";
        if (cmd is "run" or "--service") return AgentService.Run();
        Log.Console = cmd is "--run" or "--enroll" or "--once" or "--checkin-now" or "--collect-now" or "--diagnose";
        try
        {
            switch (cmd)
            {
                case "--version":
                    Console.WriteLine(AgentVersion.Current);
                    return 0;
                case "--status":
                    return Status();
                case "--identify":
                {
                    var doc = AgentWorker.CollectDocument(out _);
                    var o = doc.Os;
                    Console.WriteLine($"Distribution:  {o.Distribution} ({o.DistributionFamily} family)\nName:          {o.Name}\nVersion:       {o.Version}{(o.DisplayVersion is null ? "" : " (" + o.DisplayVersion + ")")}{(o.Lts == true ? " LTS" : "")}\n" +
                                      $"Edition:       {o.EditionId}\nInstallation:  {o.InstallationType} ({(o.IsServer == true ? "server" : "desktop")})\nDesktop:       {o.DesktopEnvironment ?? "none"}\n" +
                                      $"Kernel:        {o.Kernel}\nArchitecture:  {o.Architecture}\nPackages:      {doc.Software.Count} ({o.PackageManager})\nDevice:        {doc.Device.Manufacturer} {doc.Device.Model} ({doc.Device.Chassis}, {doc.Device.Category})");
                    return 0;
                }
                case "--collect":
                {
                    // Local diagnostics: this device's inventory as JSON (nothing is sent). "-" writes to standard output.
                    var doc = AgentWorker.CollectDocument(out var summary);
                    var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                    var file = args.Length > 1 ? args[1] : "zentriq-inventory.json";
                    if (file == "-") { Console.WriteLine(json); return 0; }
                    File.WriteAllText(file, json);
                    Console.WriteLine($"{summary}\nWritten to {Path.GetFullPath(file)}");
                    return 0;
                }
                case "--enroll":
                {
                    if (!RequireRoot()) return 1;
                    if (args.Length < 2) { Console.Error.WriteLine("Usage: sudo zentriq-agent --enroll <code> --server <server address> [--checkin <minutes>] [--inventory <minutes>] [--proxy http://host:port]"); return 2; }
                    var state = AgentState.Load();
                    var server = Option(args, "--server");
                    if (AgentCodes.NormalizeEnrollment(args[1]) is null && !AgentProtocol.TryParseCode(args[1], out _, out _))
                    { Console.Error.WriteLine("The enrollment code must have 20 letters and digits (copy it from Agents > Enrollment codes)."); return 2; }
                    if (AgentCodes.NormalizeEnrollment(args[1]) is not null && AgentWorker.NormalizeServer(server ?? state.ServerUrl) is null)
                    { Console.Error.WriteLine("Give the server address: --server https://<server>:8444"); return 2; }
                    state.EnrollmentCode = args[1].Trim().Trim('"');
                    if (server is not null) state.EnrollmentServer = AgentWorker.NormalizeServer(server);
                    if (int.TryParse(Option(args, "--checkin"), out var ci) && ci is >= 2 and <= 1440) state.InstallCheckinMinutes = ci;
                    if (int.TryParse(Option(args, "--inventory"), out var inv) && inv is >= 30 and <= 10080) state.InstallInventoryMinutes = inv;
                    if (Option(args, "--proxy") is { } proxy) state.ProxyUrl = proxy;
                    state.Save();
                    var r = new AgentWorker().RunOnce(CancellationToken.None);
                    Console.WriteLine(r.Message);
                    if (r.Ok && Shell.Systemd) Shell.Run("systemctl", ["--no-block", "restart", Paths.ServiceName + ".service"]);
                    return r.Ok ? 0 : 1;
                }
                case "--once":
                case "--checkin-now":
                case "--collect-now":
                {
                    if (!RequireRoot()) return 1;
                    var r = new AgentWorker().RunOnce(CancellationToken.None, forceInventory: cmd == "--collect-now");
                    Console.WriteLine(r.Message);
                    return r.Ok ? 0 : 1;
                }
                case "--diagnose":
                    return Diagnose();
                case "--uninstall":
                    return Uninstall(args.Length > 1 ? args[1] : null);
                case "--check-uninstall":
                    return CheckUninstall();
                case "--stop":
                    if (!RequireRoot()) return 1;
                    if (args.Length < 2 && Protection.Load().Active) { Console.Error.WriteLine("Usage: sudo zentriq-agent --stop <code>   (a one-hour code from the Zentriq console)"); return 2; }
                    return ServiceControl.StopWithCode(args.Length > 1 ? args[1] : null);
                case "--enable":
                    return RequireRoot() ? ServiceControl.Enable() : 1;
                case "--disable":
                    if (!RequireRoot()) return 1;
                    if (args.Length < 2 && Protection.Load().Active) { Console.Error.WriteLine("Usage: sudo zentriq-agent --disable <code>   (a one-hour code from the Zentriq console)"); return 2; }
                    return ServiceControl.DisableWithCode(args.Length > 1 ? args[1] : null);
                case "--restart":
                    if (!RequireRoot()) return 1;
                    ServiceControl.RestartSelf();
                    Console.WriteLine("The Zentriq Agent service is restarting.");
                    return 0;
                case "--protection":
                {
                    var p = Protection.Load();
                    Console.WriteLine(!p.Enabled ? "Tamper protection: off" : p.Paused ? $"Tamper protection: paused until {p.PausedUntil:yyyy-MM-dd HH:mm} UTC" :
                        $"Tamper protection: on (stop with 'zentriq-agent --stop <code>', remove with 'zentriq-agent --uninstall <code>'){(StopGuard.Applied ? "" : " - not applied yet")}");
                    return 0;
                }
                case "--watchdog":
                    return ServiceControl.Watchdog();
                // Software Deployment: the package-manager hooks, requests and the state on this device.
                case "--check-dpkg":
                    return AppControlAgent.CheckDpkg();
                case "--check-packages":
                    return AppControlAgent.CheckPackages(Console.In, args.Length > 1 ? args[1] : "package manager");
                case "--zypp-plugin":
                    return AppControlAgent.ZyppPlugin();
                case "--request":
                    return AppControlAgent.Request(args);
                case "--install-hooks":
                    if (!RequireRoot()) return 1;
                    AppControlAgent.PrepareSpools();
                    if (AppControlAgent.Policy.Mode == "off") { PackageHooks.Remove(); Console.WriteLine("Application control is off: no hooks."); return 0; }
                    PackageHooks.Install();
                    Console.WriteLine(PackageHooks.Installed ? "Package-manager hooks installed." : "No supported package manager found (dpkg, dnf 4, zypper).");
                    return PackageHooks.Installed ? 0 : 1;
                case "--cleanup-hooks":
                    PackageHooks.Remove();
                    RemoteGuard.Release();
                    return 0;
                case "--app-control":
                {
                    var pol = AppControlAgent.Policy;
                    Console.WriteLine($"Application control: {pol.Mode} (policy {pol.Revision}, {pol.Rules.Count} rule(s)); package-manager hooks {(PackageHooks.Installed ? "installed" : "not installed")}; remote support protection {RemoteGuard.State}.");
                    if (args.Length > 2 && args[1] == "--test")
                    {
                        var d = Zentriq.Inventory.AppControl.Evaluate(pol, new Zentriq.Inventory.SoftwareCandidate { Name = args[2], Platform = "linux" });
                        Console.WriteLine($"{args[2]}: {(d.Approved ? "allowed" : "not approved")} - {d.Reason}");
                    }
                    return 0;
                }
                case "--run":
                {
                    if (!RequireRoot()) return 1;
                    using var cts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                    AgentService.Loop(cts.Token);
                    return 0;
                }
                default:
                    Console.WriteLine("Zentriq Agent " + AgentVersion.Current + " for Linux\n" +
                        "  --status             show enrollment and last check-in\n" +
                        "  --enroll <code> --server <address> [--checkin <min>] [--inventory <min>] [--proxy http://host:port]\n" +
                        "                       enroll with an enrollment code from the console (Agents page)\n" +
                        "  --identify           show what the agent detects: distribution, edition, server or desktop\n" +
                        "  --checkin-now        contact the server now (also --once); --collect-now also sends inventory\n" +
                        "  --collect <file|->   write this device's inventory as JSON (nothing is sent)\n" +
                        "  --diagnose           check service, enrollment, certificate and the connection to the server\n" +
                        "  --app-control        application control on this device (mode, approved rules, hooks)\n" +
                        "  --request <software> [--version <v>] [--reason <text>]\n" +
                        "                       ask your administrator to approve software that was blocked (any user)\n" +
                        "  --uninstall <code>   remove the agent (code from the console, valid for 1 hour, when protection is on)\n" +
                        "  --stop <code>        stop the agent until the device restarts (code from the console when protection is on)\n" +
                        "  --disable <code>     disable the agent until --enable (code from the console when protection is on)\n" +
                        "  --enable             enable and start the agent service\n" +
                        "  --restart            restart the agent service (no code needed)\n" +
                        "  --protection         show whether tamper protection is on\n" +
                        "  --run                run in the foreground (troubleshooting)\n" +
                        "  --version            show the version");
                    return cmd is "--help" or "-h" ? 0 : 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + AgentWorker.Describe(ex));
            return 1;
        }
    }

    private static bool RequireRoot()
    {
        if (Shell.IsRoot) return true;
        Console.Error.WriteLine("Run this as root (sudo zentriq-agent ...).");
        return false;
    }

    internal static string? Option(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Status()
    {
        var s = AgentState.Load();
        var p = Protection.Load();
        Console.WriteLine($"Zentriq Agent {AgentVersion.Current} ({Platform.Id}, installed as {AgentUpdater.InstalledFormat()})\n" +
                          $"Status:        {s.Status}{(s.AgentId is null ? "" : " (agent " + s.AgentId + ")")}\nServer:        {s.ServerUrl ?? s.EnrollmentServer ?? "-"}\n" +
                          $"Last check-in: {(s.LastCheckinUtc is { } c ? c.ToString("u") : "never")}\nLast inventory:{(s.LastInventoryUtc is { } i ? " " + i.ToString("u") : " never")}\n" +
                          $"Intervals:     check-in {s.CheckinMinutes} min, inventory {s.InventoryMinutes} min\n" +
                          $"Protection:    {(!p.Enabled ? "off" : p.Paused ? "paused" : "on")}\n" +
                          (s.LastError is null ? "" : $"Last error:    {s.LastError}\n") +
                          (s.OfflineSinceUtc is { } off ? $"Offline since: {off:u}\n" : ""));
        return s.Enrolled ? 0 : 1;
    }

    private static int Diagnose()
    {
        var s = AgentState.Load();
        var ok = true;
        void Line(bool good, string text) { Console.WriteLine((good ? "[ OK ] " : "[FAIL] ") + text); ok &= good; }
        Line(Shell.IsRoot, Shell.IsRoot ? "running as root" : "not running as root (run with sudo)");
        if (Shell.Systemd)
        {
            var active = Shell.Run("systemctl", ["is-active", Paths.ServiceName + ".service"]).Output.Trim();
            Line(active == "active", "service zentriq-agent: " + active);
        }
        Line(s.Enrolled, "enrollment: " + s.Status + (s.LastError is null ? "" : " - " + s.LastError));
        using var cert = CertificateStore.Find(s.CertThumbprint);
        Line(cert is not null, cert is null ? "agent certificate missing" : $"agent certificate valid until {cert.NotAfter:yyyy-MM-dd}");
        if (s.ServerUrl is not null && cert is not null)
        {
            using var c = new ServerClient(s.ServerUrl, s.ServerPins, cert, s.ProxyUrl, timeout: TimeSpan.FromSeconds(20));
            try { c.PresenceAsync(null, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token).GetAwaiter().GetResult(); Line(true, "server " + s.ServerUrl + " answers (mutual TLS, pinned key)"); }
            catch (Exception ex) { Line(false, "server " + s.ServerUrl + ": " + AgentWorker.Describe(ex)); }
        }
        return ok ? 0 : 1;
    }

    /// <summary>zentriq-agent --uninstall &lt;code&gt;: checks the code, allows the package removal for 15 minutes and removes it.</summary>
    private static int Uninstall(string? code)
    {
        if (!RequireRoot()) return 1;
        var p = Protection.Load();
        if (p.Enabled && !p.Verify(code, out var error)) { Console.Error.WriteLine(error); return 1; }
        File.WriteAllText(Paths.UninstallAllowed, DateTime.UtcNow.ToString("o"));
        if (p.Enabled) Events.Record("control", "Agent removal started with a code on the device.");
        // Say goodbye: the server shows the device offline and keeps its record.
        var format = AgentUpdater.InstalledFormat();
        var r = format switch
        {
            "deb" => Shell.Run("apt-get", ["-y", "purge", Paths.ServiceName], TimeSpan.FromMinutes(10)),
            "rpm" => Shell.Exists("dnf") ? Shell.Run("dnf", ["-y", "remove", Paths.ServiceName], TimeSpan.FromMinutes(10))
                   : Shell.Exists("zypper") ? Shell.Run("zypper", ["--non-interactive", "remove", Paths.ServiceName], TimeSpan.FromMinutes(10))
                   : Shell.Run("rpm", ["-e", Paths.ServiceName], TimeSpan.FromMinutes(10)),
            _ => Shell.Run(Path.Combine(Paths.Install, "uninstall.sh"), [], TimeSpan.FromMinutes(5)),
        };
        Console.WriteLine(r.Ok ? "The Zentriq Agent was removed." : "Removal failed: " + r.Text);
        return r.Ok ? 0 : 1;
    }

    /// <summary>
    /// Run by the package before it removes the agent (not on upgrades): refuses while protection is on, unless
    /// --uninstall allowed it in the last 15 minutes or ZENTRIQ_UNINSTALL_CODE holds a valid code.
    /// </summary>
    private static int CheckUninstall()
    {
        var p = Protection.Load();
        if (!p.Enabled) return 0;
        try
        {
            if (File.Exists(Paths.UninstallAllowed) && DateTime.TryParse(File.ReadAllText(Paths.UninstallAllowed).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                && at.ToUniversalTime() > DateTime.UtcNow.AddMinutes(-15)) return 0;
        }
        catch (IOException) { }
        var code = Environment.GetEnvironmentVariable("ZENTRIQ_UNINSTALL_CODE");
        if (!string.IsNullOrWhiteSpace(code))
        {
            if (p.Verify(code, out var error)) { Events.Record("control", "Agent package removed with a code."); return 0; }
            Console.Error.WriteLine("Zentriq Agent: " + error);
            return 1;
        }
        Console.Error.WriteLine("The Zentriq Agent is protected: removing it needs a one-hour code from the Zentriq console (Agents › Uninstall code).\n" +
                                "Run: sudo zentriq-agent --uninstall <code>");
        Events.Record("tamper", "Someone tried to remove the agent package without a code.");
        return 1;
    }
}

/// <summary>The systemd service: check-in loop and live connection until SIGTERM.</summary>
internal static class AgentService
{
    private static readonly AutoResetEvent ServerWake = new(false);

    public static void RequestServerWake() => ServerWake.Set();

    private static int _forceInventory;
    /// <summary>Inventory at the next check-in (soon): software was installed or removed.</summary>
    public static void RequestInventory() { Interlocked.Exchange(ref _forceInventory, 1); ServerWake.Set(); }

    public static int Run()
    {
        Log.Journal = true;
        if (!Shell.IsRoot) { Log.Error("The Zentriq Agent service must run as root."); return 1; }
        using var cts = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });
        Loop(cts.Token);
        Log.Info("Zentriq Agent stopped.");
        return 0;
    }

    public static void Loop(CancellationToken ct)
    {
        Paths.EnsureData();
        Log.Info($"Zentriq Agent {AgentVersion.Current} started ({Platform.Id}).");
        // A stop with a code lasts until the agent starts again (restart of the device, or --enable).
        var p = Protection.Load();
        if (p.StoppedByCode is not null) { p.StoppedByCode = null; if (p.Enabled) p.Save(); }
        try { File.Delete(Paths.UninstallAllowed); } catch (IOException) { }
        StopGuard.Sync(p);
        AppControlAgent.OnServiceStart();
        NetworkChange.NetworkAddressChanged += (_, _) => { Presence.Retry.Set(); ServerWake.Set(); };
        var presence = new Thread(() => Presence.Loop(ct)) { IsBackground = true, Name = "presence" };
        presence.Start();
        var worker = new AgentWorker();
        // A short random delay so a fleet restarting together does not arrive at once.
        if (WaitHandle.WaitAny([ct.WaitHandle, ServerWake], TimeSpan.FromSeconds(Random.Shared.Next(5, 30))) == 0) { presence.Join(TimeSpan.FromSeconds(5)); return; }
        var lastProtection = DateTime.UtcNow;
        var next = DateTime.MinValue;
        var woke = true;
        while (!ct.IsCancellationRequested)
        {
            if (woke || DateTime.UtcNow >= next)
            {
                var r = worker.RunOnce(ct, forceInventory: Interlocked.Exchange(ref _forceInventory, 0) == 1);
                Log.Info((r.Ok ? "" : "Not done: ") + r.Message + $" Next in {Math.Max(1, (int)Math.Round(r.NextRun.TotalMinutes))} min.");
                next = DateTime.UtcNow + r.NextRun;
            }
            // Re-check the stop protection now and then (a pause ends by itself).
            if (DateTime.UtcNow - lastProtection > TimeSpan.FromMinutes(5)) { StopGuard.Sync(Protection.Load()); RemoteGuard.Sync(); lastProtection = DateTime.UtcNow; }
            var wait = next - DateTime.UtcNow;
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            if (wait > TimeSpan.FromMinutes(5)) wait = TimeSpan.FromMinutes(5);
            var i = WaitHandle.WaitAny([ct.WaitHandle, ServerWake], wait);
            if (i == 0) break;
            woke = i == 1;
        }
        presence.Join(TimeSpan.FromSeconds(5));
    }
}
