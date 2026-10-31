using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>
/// One agent cycle: enroll (if a code is present) → check in (connectivity, console requests, schedule) → renew
/// certificate → inventory when due. When the server does not answer at its usual address, the agent tries the server's
/// other names and addresses and then looks for it on the local network, trusting only the pinned server key.
/// </summary>
internal sealed class AgentWorker
{
    public sealed class Outcome
    {
        public TimeSpan NextRun { get; set; }
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
    }

    private int _failures;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    public Outcome RunOnce(CancellationToken ct, bool forceInventory = false)
    {
        var state = AgentState.Load();
        try
        {
            if (!string.IsNullOrEmpty(state.EnrollmentCode))
            {
                Enroll(state, ct);
                if (state.Enrolled) Presence.Retry.Set();   // live connection right after enrollment
            }
            if (!state.Enrolled)
            {
                var msg = state.Status == "revoked"
                    ? "This agent was revoked in the console. Reinstall or run 'ZentriqAgent.exe --enroll <code> --server <address>' with a new enrollment code."
                    : "Not enrolled. Install with SERVERURL=... ENROLLMENTCODE=... or run 'ZentriqAgent.exe --enroll <code> --server <address>'.";
                state.LastError = msg;
                state.Save();
                return Done(false, msg, TimeSpan.FromMinutes(state.Status == "revoked" ? 360 : Math.Min(30, state.CheckinMinutes)));
            }

            var cert = CertificateStore.Find(state.CertThumbprint);
            if (cert is null)
            {
                state.Status = "pending";
                state.LastError = "The agent certificate is missing from Local Computer > Personal. Re-enroll the agent.";
                state.Save();
                return Done(false, state.LastError, TimeSpan.FromMinutes(30));
            }

            var (host, os, _) = HostFacts();
            var (client, resp) = CheckIn(state, cert, host, os, ct);
            using (client)
            {
                if (state.OfflineSinceUtc is { } since)
                    Log.Info($"Connection to the server restored after {Describe(DateTime.UtcNow - since)} (last contact {since:u}).");
                state.OfflineSinceUtc = null;
                state.LastCheckinUtc = DateTime.UtcNow;
                if (resp.CheckinMinutes > 0) state.CheckinMinutes = Math.Max(2, resp.CheckinMinutes);
                if (resp.InventoryMinutes > 0) state.InventoryMinutes = Math.Max(30, resp.InventoryMinutes);
                if (resp.ServerUrls is { Count: > 0 }) state.ServerUrls = resp.ServerUrls.Where(Usable).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
                MergePins(state, resp.ServerPins);
                ApplyProtection(resp);

                if (resp.RenewCertificate) Renew(state, client, ct);
                if (resp.Actions is { Count: > 0 }) RunActions(client, resp.Actions, ct);
                else AgentUpdater.CleanUp();

                state.LastError = null;
                var due = forceInventory || resp.InventoryDue || state.LastInventoryUtc is null || state.LastInventoryUtc < DateTime.UtcNow.AddMinutes(-state.InventoryMinutes);
                if (due)
                {
                    byte[] payload;
                    try { payload = Collect(out var summary); Log.Info("Inventory collected: " + summary); }
                    catch (Exception ex) { Log.Error("Inventory collection failed", ex); throw; }
                    Upload(client, state, payload, ct);
                }
                else if (File.Exists(AgentPaths.QueuedInventory))
                {
                    Upload(client, state, File.ReadAllBytes(AgentPaths.QueuedInventory), ct);
                }
                state.Save();
                _failures = 0;
                return Done(true, due ? "Checked in and sent inventory." : "Checked in.", Jitter(TimeSpan.FromMinutes(state.CheckinMinutes)));
            }
        }
        catch (ServerException ex) when (ex.Status == HttpStatusCode.Forbidden && state.Enrolled)
        {
            Log.Warn("Server refused this agent: " + ex.Message);
            if (ex.Message.IndexOf("revoked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                CertificateStore.Remove(state.CertThumbprint);
                state.Status = "revoked";
                state.CertThumbprint = null;
                if (UninstallProtection.CanManage) try { UninstallProtection.Apply(null); } catch (Exception pe) { Log.Warn("Could not remove uninstall protection: " + pe.Message); }
            }
            state.LastError = ex.Message;
            state.Save();
            return Done(false, ex.Message, TimeSpan.FromHours(6));
        }
        catch (ServerException ex) when (ex.Status == HttpStatusCode.Unauthorized && state.Enrolled)
        {
            Log.Warn("Server no longer accepts this agent's certificate: " + ex.Message);
            state.Status = "pending";
            state.LastError = ex.Message + " Re-enroll the agent with a new enrollment code.";
            state.Save();
            return Done(false, state.LastError, TimeSpan.FromHours(1));
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            var msg = Describe(ex);
            Log.Warn("Cycle failed: " + msg);
            state.LastError = msg;
            if (ex is ServerUnreachableException && state.Enrolled && state.OfflineSinceUtc is null)
            {
                state.OfflineSinceUtc = state.LastCheckinUtc ?? DateTime.UtcNow;
                Log.Warn($"Lost connection to the server (last contact {state.OfflineSinceUtc:u}). Retrying; inventory is kept until the server is reachable again.");
            }
            try { state.Save(); } catch (Exception) { }
            _failures++;
            // Retry after 1, 2, 4, 8 and then every 15 minutes (never later than the check-in interval), so a restored
            // connection is noticed quickly; network changes and resume from sleep trigger an immediate retry.
            var backoff = TimeSpan.FromMinutes(Math.Min(Math.Min(15, state.CheckinMinutes), Math.Pow(2, Math.Min(_failures - 1, 10))));
            return Done(false, msg, Jitter(backoff));
        }
    }

    // ---- connectivity ----------------------------------------------------------------------------------------

    /// <summary>
    /// Checks in at the usual address; if the server does not answer there, at its other names and addresses, and then at
    /// the addresses servers on the local network announce. Switches to the address that answered.
    /// </summary>
    private (ServerClient Client, CheckinResponse Response) CheckIn(AgentState state, System.Security.Cryptography.X509Certificates.X509Certificate2 cert, string host, string os, CancellationToken ct)
    {
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception? last = null;
        IEnumerable<string> Candidates()
        {
            yield return state.ServerUrl!;
            foreach (var u in state.ServerUrls) yield return u;
            foreach (var u in Discover(state, ct)) yield return u;
        }
        foreach (var url in Candidates())
        {
            if (string.IsNullOrWhiteSpace(url) || !tried.Add(url.TrimEnd('/'))) continue;
            var client = new ServerClient(url, state.ServerPins, cert, state.ProxyUrl, timeout: ConnectTimeout);
            try
            {
                var events = UninstallProtection.CanManage ? TamperGuard.PendingEvents() : null;
                var resp = client.CheckinAsync(host, os, state.LastError, state.OfflineSinceUtc, UninstallProtection.CanManage ? UninstallProtection.Enabled : (bool?)null, ct, events).GetAwaiter().GetResult();
                if (events is { Count: > 0 }) TamperGuard.ClearEvents(events.Count);
                if (!string.Equals(url.TrimEnd('/'), state.ServerUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"The server answers at {url} (not at {state.ServerUrl}); using it from now on.");
                    state.ServerUrl = url.TrimEnd('/');
                }
                return (client, resp);
            }
            catch (Exception ex) when (IsConnectivity(ex, ct))
            {
                client.Dispose();
                last = ex;
                Log.Info($"No answer from {url}: {Describe(ex)}");
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        throw new ServerUnreachableException($"Server unreachable at {string.Join(", ", tried.Take(4))}{(tried.Count > 4 ? ", …" : "")}: {(last is null ? "no address" : Describe(last))}");
    }

    /// <summary>Connection-level failures (no answer, DNS, TLS, timeout): try the next address. HTTP errors are answers.</summary>
    private static bool IsConnectivity(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested) return false;
        var e = ex;
        while (e is AggregateException ae && ae.InnerException is not null) e = ae.InnerException;
        return e is HttpRequestException || e is WebException || e is SocketException || e is IOException || e is OperationCanceledException;
    }

    /// <summary>Asks servers on the local network for their agent addresses (UDP broadcast, see the server's AgentDiscovery).</summary>
    internal static List<string> Discover(AgentState state, CancellationToken ct)
    {
        var found = new List<string>();
        int port;
        try { port = new Uri(state.ServerUrl!).Port; } catch (Exception) { port = 8444; }
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            udp.Client.ReceiveTimeout = 3000;
            var request = Encoding.ASCII.GetBytes("ZENTRIQ-DISCOVER/1 " + Guid.NewGuid().ToString("N"));
            foreach (var target in BroadcastAddresses())
                try { udp.Send(request, request.Length, new IPEndPoint(target, port)); } catch (SocketException) { }
            var until = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try { data = udp.Receive(ref from); }
                catch (SocketException) { break; }
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                    {
                        // The address the answer came from first: it is on this device's network.
                        found.Add($"https://{from.Address}:{port}");
                        foreach (var u in urls.EnumerateArray()) if (u.GetString() is { } s && Usable(s)) found.Add(s);
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException) { Log.Info("Local network discovery not possible: " + ex.Message); }
        if (found.Count > 0) Log.Info($"Found the server on the local network: {string.Join(", ", found.Distinct().Take(4))}.");
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var u in ni.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address) || u.IPv4Mask is null) continue;
                var a = u.Address.GetAddressBytes(); var m = u.IPv4Mask.GetAddressBytes();
                var b = new byte[4];
                for (var i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
                yield return new IPAddress(b);
            }
        }
    }

    private static bool Usable(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static void ApplyProtection(CheckinResponse resp)
    {
        if (!UninstallProtection.CanManage) return;
        try
        {
            if (!resp.UninstallProtection) UninstallProtection.Apply(null);
            else if (!string.IsNullOrEmpty(resp.UninstallKey)) UninstallProtection.Apply(Convert.FromBase64String(resp.UninstallKey));
            if (resp.UninstallProtection) UninstallProtection.ApplyCodeForAll(resp.UninstallCode);
        }
        catch (Exception ex) { Log.Warn("Could not update uninstall protection: " + ex.Message); }
    }

    // ---- enrollment --------------------------------------------------------------------------------------------

    private static void Enroll(AgentState state, CancellationToken ct)
    {
        string url, token;
        List<string> pins;
        List<string>? urls = null;
        var shortCode = AgentCodes.NormalizeEnrollment(state.EnrollmentCode);
        if (shortCode is not null)
        {
            if (NormalizeServer(state.EnrollmentServer ?? state.ServerUrl) is not { } server)
            {
                Fail(state, "The enrollment code needs the server address: install with SERVERURL=https://<server>:8444, or run 'ZentriqAgent.exe --enroll <code> --server <server>'.");
                return;
            }
            // Learn the server's key from the server itself; it must prove that it knows the code for that key.
            var nonce = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            using (var hello = new ServerClient(server, new string[0], null, state.ProxyUrl, trustAnyForHello: true, timeout: ConnectTimeout))
            {
                HelloResponse h;
                try { h = hello.HelloAsync(AgentCodes.EnrollmentId(shortCode), nonce, ct).GetAwaiter().GetResult(); }
                catch (ServerException ex) when (ex.Status == HttpStatusCode.Forbidden || ex.Status == HttpStatusCode.BadRequest) { Fail(state, "Enrollment refused: " + ex.Message); return; }
                var serverPins = h.Pins ?? new List<string>();
                var expected = AgentCodes.EnrollProof(AgentCodes.Sha256(shortCode), nonce, serverPins);
                if (hello.ObservedPin is null || !serverPins.Contains(hello.ObservedPin) || !string.Equals(expected, h.Proof, StringComparison.Ordinal))
                {
                    Fail(state, $"The server at {server} could not prove that it issued this enrollment code (wrong server, or a device in between). Check the server address.");
                    return;
                }
                url = server; token = shortCode; pins = serverPins; urls = h.ServerUrls;
            }
        }
        else if (AgentProtocol.TryParseCode(state.EnrollmentCode, out var code, out var error))
        {
            url = code!.Url; token = code.Token; pins = code.Pins;
        }
        else
        {
            Fail(state, "Enrollment code rejected: " + (error ?? "it must have 20 letters and digits."));
            return;
        }

        var (host, os, uuid) = HostFacts();
        Log.Info($"Enrolling with {url} as {host}.");
        using var key = new PendingKey();
        using var client = new ServerClient(url, pins, null, state.ProxyUrl);
        EnrollResponse resp;
        try { resp = client.EnrollAsync(token, key.CreateCsr(host), host, os, uuid, state.InstallCheckinMinutes, state.InstallInventoryMinutes, ct).GetAwaiter().GetResult(); }
        catch (ServerException ex) when (ex.Status == HttpStatusCode.Forbidden || ex.Status == HttpStatusCode.BadRequest)
        {
            // The code is invalid/expired/used up: drop it so the agent does not retry forever.
            Fail(state, "Enrollment refused: " + ex.Message);
            return;
        }
        var installed = key.Install(Convert.FromBase64String(resp.Certificate));
        var old = state.CertThumbprint;
        state.AgentId = resp.AgentId;
        state.CertThumbprint = installed.Thumbprint;
        state.ServerUrl = url.TrimEnd('/');
        state.ServerPins = pins.ToList();
        MergePins(state, resp.ServerPins);
        var allUrls = resp.ServerUrls ?? urls;
        if (allUrls is { Count: > 0 }) state.ServerUrls = allUrls.Where(Usable).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
        if (resp.CheckinMinutes > 0) state.CheckinMinutes = resp.CheckinMinutes;
        if (resp.InventoryMinutes > 0) state.InventoryMinutes = resp.InventoryMinutes;
        state.Status = "enrolled";
        state.EnrollmentCode = null;
        state.EnrollmentServer = null;
        state.LastInventoryUtc = null;
        state.OfflineSinceUtc = null;
        state.LastError = null;
        state.Save();
        if (UninstallProtection.CanManage)
            try
            {
                if (resp.UninstallProtection && !string.IsNullOrEmpty(resp.UninstallKey)) UninstallProtection.Apply(Convert.FromBase64String(resp.UninstallKey));
                else if (!resp.UninstallProtection) UninstallProtection.Apply(null);
            }
            catch (Exception ex) { Log.Warn("Could not set up uninstall protection: " + ex.Message); }
        if (!string.IsNullOrEmpty(old) && !string.Equals(old, installed.Thumbprint, StringComparison.OrdinalIgnoreCase)) CertificateStore.Remove(old);
        Log.Info($"Enrolled as agent {resp.AgentId}; certificate {installed.Thumbprint} expires {installed.NotAfter:yyyy-MM-dd}; " +
                 $"checks in every {state.CheckinMinutes} min, sends inventory every {state.InventoryMinutes} min.");
    }

    private static void Fail(AgentState state, string message)
    {
        Log.Error(message);
        state.EnrollmentCode = null;
        state.LastError = message;
        state.Save();
    }

    /// <summary>"server", "server:8444" or "https://server:8444" → https://server:8444 (agent port 8444 by default).</summary>
    internal static string? NormalizeServer(string? value)
    {
        var v = (value ?? "").Trim().Trim('"').TrimEnd('/');
        if (v.Length == 0) return null;
        if (!v.Contains("://")) v = "https://" + v;
        if (!Uri.TryCreate(v, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(u.Host)) return null;
        var port = u.IsDefaultPort && !value!.Contains(":443") ? 8444 : u.Port;
        return $"https://{u.Host}:{port}";
    }

    private static void Renew(AgentState state, ServerClient client, CancellationToken ct)
    {
        try
        {
            using var key = new PendingKey();
            var resp = client.RenewAsync(key.CreateCsr(Environment.MachineName), ct).GetAwaiter().GetResult();
            var installed = key.Install(Convert.FromBase64String(resp.Certificate));
            var old = state.CertThumbprint;
            state.CertThumbprint = installed.Thumbprint;
            state.Save();
            CertificateStore.Remove(old);
            Log.Info($"Agent certificate renewed; expires {installed.NotAfter:yyyy-MM-dd}.");
        }
        catch (Exception ex) { Log.Warn("Certificate renewal failed (will retry): " + Describe(ex)); }
    }

    private static void Upload(ServerClient client, AgentState state, byte[] payload, CancellationToken ct)
    {
        try
        {
            client.UploadInventoryAsync(payload, ct).GetAwaiter().GetResult();
            state.LastInventoryUtc = DateTime.UtcNow;
            try { File.Delete(AgentPaths.QueuedInventory); } catch (IOException) { }
            Log.Info($"Inventory uploaded ({payload.Length / 1024} KB compressed).");
        }
        catch (ServerException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            // Not stored (e.g. license device limit reached): retry at the normal inventory interval, not every cycle.
            state.LastInventoryUtc = DateTime.UtcNow;
            try { File.Delete(AgentPaths.QueuedInventory); } catch (IOException) { }
            Log.Warn("Server did not store the inventory: " + ex.Message);
        }
        catch (Exception ex) when (!(ex is ServerException se && (se.Status == HttpStatusCode.Conflict || se.Status == HttpStatusCode.BadRequest)))
        {
            Directory.CreateDirectory(AgentPaths.Queue);
            File.WriteAllBytes(AgentPaths.QueuedInventory, payload);
            Log.Warn("Inventory queued for the next check-in: " + Describe(ex));
            throw;
        }
    }

    /// <summary>Runs console-requested service actions and reports each result. Failures never stop the check-in.</summary>
    private static void RunActions(ServerClient client, List<AgentAction> actions, CancellationToken ct)
    {
        AgentAction? update = null, control = null;
        foreach (var a in actions)
        {
            if (a.Kind == "agent-update") { update = a; continue; }
            if (a.Kind == "agent-control") { control = a; continue; }
            bool ok; string? st; string msg;
            try
            {
                (ok, st, msg) = a.Kind switch
                {
                    "uninstall" => SoftwareActions.Uninstall(a.Target, a.Argument, TimeSpan.FromMinutes(20)),
                    "remote-support" => RemoteSupportActions.Run(client, a.Operation, a.Argument, ct),
                    _ => ServiceActions.Run(a.Kind, a.Target, a.Operation, TimeSpan.FromSeconds(60)),
                };
            }
            catch (Exception ex) { ok = false; st = null; msg = ex.Message; }
            Log.Info($"Action {a.Operation} {a.Target}: {(ok ? "OK" : "FAILED")} - {msg}");
            try { client.ReportActionAsync(a.Id, ok, st, msg, ct).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Warn("Could not report action result: " + ex.Message); }
        }
        // Console control of the agent itself (pause / resume protection, restart, disable): the result is reported first.
        if (control is not null) RunControl(client, control, ct);
        // The agent update runs last: Windows Installer stops this service while it replaces the files.
        if (update is not null)
        {
            bool ok; string? st; string msg;
            try { (ok, st, msg) = AgentUpdater.Start(client, update.Id, update.Target, update.Argument, ct); }
            catch (Exception ex) { ok = false; st = null; msg = "Update failed: " + Describe(ex); }
            Log.Info($"Agent update to {update.Target}: {(ok ? st : "FAILED")} - {msg}");
            if (st != "started") try { client.ReportActionAsync(update.Id, ok, st, msg, ct).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Warn("Could not report the update result: " + ex.Message); }
        }
    }

    private static void RunControl(ServerClient client, AgentAction a, CancellationToken ct)
    {
        bool ok = true; string msg; Action? after = null;
        try
        {
            if (!UninstallProtection.CanManage) throw new InvalidOperationException("The agent is not running as a service.");
            switch (a.Operation)
            {
                case "pause":
                    var hours = int.TryParse(a.Argument, out var h) ? Math.Max(1, Math.Min(24, h)) : 1;
                    msg = UninstallProtection.Enabled ? TamperGuard.Perform("pause", "from the Zentriq console", TimeSpan.FromHours(hours)) : "Tamper protection is off for all agents; nothing to pause.";
                    break;
                case "resume":
                    msg = TamperGuard.Perform("resume", "from the Zentriq console");
                    break;
                case "restart":
                    msg = "The agent service restarts now.";
                    after = () => RestartService();
                    break;
                case "disable":
                    msg = "The agent service is disabled and stops now; the device stays offline until it is enabled on the device ('ZentriqAgent.exe --enable').";
                    after = () => { if (UninstallProtection.Enabled) TamperGuard.Perform("disable", "from the Zentriq console"); else DisableService(); };
                    break;
                default:
                    ok = false; msg = "Unknown agent control " + a.Operation + "."; break;
            }
        }
        catch (Exception ex) { ok = false; msg = ex.Message; }
        Log.Info($"Agent control {a.Operation}: {(ok ? "OK" : "FAILED")} - {msg}");
        try { client.ReportActionAsync(a.Id, ok, ok ? a.Operation : null, msg, ct).GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warn("Could not report the agent control result: " + ex.Message); }
        if (ok) after?.Invoke();
    }

    private static void RestartService()
    {
        StopGate.AuthorizeInternal("restart from the Zentriq console");
        // A separate process stops and starts this service (this process ends with the stop).
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cmd, $"/c ping -n 3 127.0.0.1 >nul & net stop {Program.ServiceName} & net start {Program.ServiceName}")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
    }

    private static void DisableService()
    {
        StopGate.AuthorizeInternal("disabled from the Zentriq console");
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(sc, $"config {Program.ServiceName} start= disabled") { UseShellExecute = false, CreateNoWindow = true })) p?.WaitForExit(15000);
        try { using var svc = new System.ServiceProcess.ServiceController(Program.ServiceName); svc.Stop(); } catch (Exception ex) { Log.Warn("Could not stop the agent service: " + ex.Message); }
    }

    public static byte[] Collect(out string summary)
    {
        var old = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try
        {
            var doc = WindowsCollector.Collect(new LocalWmiSource(), new LocalRegistrySource(), "agent", Program.Version, includeServices: true);
            summary = $"{doc.Device.Hostname}, {doc.Os.Name}, {doc.Software.Count} software, {doc.Patches.Count} updates, {doc.Errors.Count} section error(s)";
            foreach (var e in doc.Errors) Log.Warn("Inventory section: " + e);
            return ServerClient.Compress(doc);
        }
        finally { Thread.CurrentThread.Priority = old; }
    }

    private static (string Host, string Os, string? Uuid) HostFacts()
    {
        string os = "Windows";
        string? uuid = null;
        try
        {
            var wmi = new LocalWmiSource();
            var o = wmi.Query(@"root\cimv2", "SELECT Caption FROM Win32_OperatingSystem").FirstOrDefault();
            if (o is not null && o.TryGetValue("Caption", out var c) && c is string cs) os = cs.Trim();
            var p = wmi.Query(@"root\cimv2", "SELECT UUID FROM Win32_ComputerSystemProduct").FirstOrDefault();
            if (p is not null && p.TryGetValue("UUID", out var u) && u is string us) uuid = us.Trim();
        }
        catch (Exception ex) { Log.Warn("WMI unavailable for host facts: " + ex.Message); }
        return (Environment.MachineName, os, uuid);
    }

    private static void MergePins(AgentState state, List<string>? pins)
    {
        if (pins is null || pins.Count == 0) return;
        // Keep the newest pins first; retain up to 4 so a server certificate change is picked up before the old one expires.
        state.ServerPins = pins.Concat(state.ServerPins).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Take(4).ToList();
    }

    internal static string Describe(Exception ex)
    {
        var e = ex;
        while (e is AggregateException ae && ae.InnerException is not null) e = ae.InnerException;
        if (e is HttpRequestException hre && hre.InnerException is WebException we)
            return "Server unreachable: " + we.Message;
        if (e is System.Threading.Tasks.TaskCanceledException) return "Server unreachable: no answer within " + (int)ConnectTimeout.TotalSeconds + " seconds";
        return e.Message;
    }

    private static string Describe(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} day(s) {t.Hours} h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, (int)t.TotalMinutes)} min";

    private static readonly Random Rng = new Random();
    private static TimeSpan Jitter(TimeSpan t) => TimeSpan.FromMilliseconds(t.TotalMilliseconds * (0.9 + Rng.NextDouble() * 0.2));
    private static Outcome Done(bool ok, string message, TimeSpan next) => new Outcome { Ok = ok, Message = message, NextRun = next };
}

/// <summary>The server did not answer at any known address.</summary>
internal sealed class ServerUnreachableException : Exception
{
    public ServerUnreachableException(string message) : base(message) { }
}
