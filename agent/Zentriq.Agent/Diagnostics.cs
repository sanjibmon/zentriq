using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Threading;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>
/// Commands for an administrator on the device when the agent does not show up in the console:
/// <c>--checkin-now</c> (contact the server now, through the running service), <c>--restart</c> and <c>--diagnose</c>
/// (service, enrollment, certificate, name resolution, port and server key for every known server address).
/// </summary>
internal static class Diagnostics
{
    /// <summary>--checkin-now / --once / --collect-now: the running service checks in at once and reports the result.</summary>
    public static int CheckinNow(bool inventory)
    {
        if (ServiceRunning())
        {
            Console.WriteLine(inventory ? "Asking the Zentriq Agent service to check in and send inventory now…" : "Asking the Zentriq Agent service to check in now…");
            var code = TamperGuard.Client(inventory ? "collect" : "checkin", null);
            if (code != 0) Console.WriteLine("If this keeps failing, run: ZentriqAgent.exe --diagnose");
            return code;
        }
        Console.WriteLine("The Zentriq Agent service is not running; checking in from this window instead.");
        var r = new AgentWorker().RunOnce(CancellationToken.None, forceInventory: inventory);
        Console.WriteLine(r.Message);
        if (!r.Ok) Console.WriteLine("Run 'ZentriqAgent.exe --diagnose' for details. Start the service with 'ZentriqAgent.exe --enable'.");
        return r.Ok ? 0 : 1;
    }

    public static bool ServiceRunning()
    {
        try { using var sc = new ServiceController(Program.ServiceName); return sc.Status == ServiceControllerStatus.Running; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>--diagnose: checks everything between this device and the server and says what to fix.</summary>
    public static int Diagnose()
    {
        var problems = new List<string>();
        void Ok(string what) => Console.WriteLine("  [ OK ] " + what);
        void Bad(string what, string fix) { Console.WriteLine("  [FAIL] " + what); problems.Add(fix); }
        void Info(string what) => Console.WriteLine("  [info] " + what);

        Console.WriteLine($"Zentriq Agent {Program.Version} on {Environment.MachineName} — diagnosis {DateTime.Now:yyyy-MM-dd HH:mm}");
        var s = AgentState.Load();

        Console.WriteLine("\nService");
        try
        {
            using var sc = new ServiceController(Program.ServiceName);
            if (sc.Status == ServiceControllerStatus.Running) Ok("ZentriqAgent service is running");
            else Bad($"ZentriqAgent service is {sc.Status.ToString().ToLowerInvariant()}", "Start the service: ZentriqAgent.exe --enable");
            var start = TamperGuard.StartType();
            if (start == 4) Bad("startup type is Disabled", "Enable the service: ZentriqAgent.exe --enable");
        }
        catch (InvalidOperationException) { Bad("ZentriqAgent service is not installed", "Install the agent again (ZentriqAgent-x64.msi)."); }

        Console.WriteLine("\nEnrollment");
        if (s.Enrolled) Ok($"enrolled (agent ID {s.AgentId})");
        else if (s.Status == "revoked") Bad("this agent was revoked in the console", "Enroll again: ZentriqAgent.exe --enroll <code> --server <address> (code from Agents › New enrollment code).");
        else if (!string.IsNullOrEmpty(s.EnrollmentCode)) Bad("enrollment pending (the code has not been accepted yet)", "Check the server address and the code; the service retries automatically.");
        else Bad("not enrolled", "Enroll: ZentriqAgent.exe --enroll <code> --server <address>.");
        X509Certificate2? cert = null;
        if (s.Enrolled)
        {
            cert = CertificateStore.Find(s.CertThumbprint);
            if (cert is null) Bad("the agent certificate is missing from Local Computer › Personal", "Enroll again with a new enrollment code.");
            else if (cert.NotAfter < DateTime.Now) Bad($"the agent certificate expired on {cert.NotAfter:yyyy-MM-dd}", "Enroll again with a new enrollment code.");
            else Ok($"certificate valid until {cert.NotAfter:yyyy-MM-dd}");
        }
        Info($"last check-in: {(s.LastCheckinUtc is { } lc ? lc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never")}, last inventory: {(s.LastInventoryUtc is { } li ? li.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never")}");
        if (s.OfflineSinceUtc is { } off) Info($"the server has not answered since {off.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrEmpty(s.LastError)) Info("last error: " + s.LastError);

        Console.WriteLine("\nNetwork");
        if (!string.IsNullOrEmpty(s.ProxyUrl)) Info($"web proxy: {s.ProxyUrl} (remove it unless the device can reach the server only through it)");
        var urls = new[] { s.ServerUrl ?? s.EnrollmentServer }.Concat(s.ServerUrls).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
        if (urls.Count == 0) Bad("no server address", "Enroll with --server <address>.");
        var anyOk = false;
        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) { Bad($"{url}: not a valid address", "Enroll again with a correct --server address."); continue; }
            Console.WriteLine($"  {url}");
            IPAddress[] addrs;
            try { addrs = IPAddress.TryParse(u.Host, out var ip) ? new[] { ip } : Dns.GetHostAddresses(u.Host); }
            catch (SocketException ex) { Bad($"    name {u.Host} cannot be resolved ({ex.Message})", $"Check DNS for {u.Host}, or use the server's IP address."); continue; }
            Ok($"    {u.Host} → {string.Join(", ", addrs.Select(a => a.ToString()).Take(4))}");
            using var tcp = new TcpClient();
            var sw = Stopwatch.StartNew();
            try
            {
                var connect = tcp.ConnectAsync(u.Host, u.Port);
                if (!connect.Wait(TimeSpan.FromSeconds(8))) throw new System.TimeoutException("no answer within 8 seconds");
            }
            catch (Exception ex)
            {
                var msg = (ex is AggregateException ae ? ae.InnerException ?? ex : ex).Message;
                Bad($"    port {u.Port}: {msg}", $"Allow TCP {u.Port} from this device to the Zentriq server (Windows Firewall on the server, network firewalls, VPN).");
                continue;
            }
            Ok($"    port {u.Port} reachable ({sw.ElapsedMilliseconds} ms)");
            try
            {
                X509Certificate? seen = null;
                using var ssl = new SslStream(tcp.GetStream(), false, (_, c, _, _) => { seen = c; return true; });
                ssl.AuthenticateAsClient(u.Host);
                var pin = seen is null ? null : AgentProtocol.SpkiSha256(seen.GetRawCertData());
                if (pin is not null && s.ServerPins.Contains(pin)) { Ok("    server key matches the pinned Zentriq server key"); anyOk = true; }
                else if (s.ServerPins.Count == 0) { Info("    no pinned server key yet (set at enrollment)"); anyOk = true; }
                else Bad("    the server presented a different key than the pinned Zentriq server (another server, a TLS-inspecting proxy, or a replaced certificate)",
                    "If the server certificate was replaced after the old one expired, enroll the agent again; exclude the server from TLS inspection.");
            }
            catch (Exception ex) { Bad($"    TLS handshake failed: {ex.Message}", "Check that the address points to the Zentriq agent port (8444 by default) and no proxy intercepts it."); }
        }

        Console.WriteLine("\nTamper protection");
        try { TamperGuard.Client("status", null); } catch (Exception) { Info("unknown (service not reachable)"); }

        if (anyOk && s.Enrolled && cert is not null && ServiceRunning())
        {
            Console.WriteLine("\nCheck-in");
            if (TamperGuard.Client("checkin", null) != 0) problems.Add("The server refused or did not complete the check-in; see the message above and C:\\ProgramData\\Zentriq\\Agent\\Logs\\agent.log.");
        }

        Console.WriteLine();
        if (problems.Count == 0) { Console.WriteLine("No problem found. The device should show as online in the Zentriq console."); return 0; }
        Console.WriteLine("What to fix:");
        foreach (var p in problems.Distinct()) Console.WriteLine("  - " + p);
        Console.WriteLine("Log: " + Path.Combine(Log.Directory, "agent.log"));
        return 1;
    }
}
