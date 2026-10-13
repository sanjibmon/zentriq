using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>One agent cycle: enroll (if a code is present) → check in → renew certificate → inventory.</summary>
internal sealed class AgentWorker
{
    public sealed class Outcome
    {
        public TimeSpan NextRun { get; set; }
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
    }

    private int _failures;

    public Outcome RunOnce(CancellationToken ct, bool forceInventory = false)
    {
        var state = AgentState.Load();
        try
        {
            if (!string.IsNullOrEmpty(state.EnrollmentCode))
                Enroll(state, ct);
            if (!state.Enrolled)
            {
                var msg = state.Status == "revoked"
                    ? "This agent was revoked in the console. Reinstall or run 'ZentriqAgent.exe --enroll <code>' with a new enrollment code."
                    : "Not enrolled. Install with ENROLLMENTCODE=... or run 'ZentriqAgent.exe --enroll <code>'.";
                state.LastError = msg;
                state.Save();
                return Done(false, msg, TimeSpan.FromMinutes(state.Status == "revoked" ? 360 : 30));
            }

            var cert = CertificateStore.Find(state.CertThumbprint);
            if (cert is null)
            {
                state.Status = "pending";
                state.LastError = "The agent certificate is missing from Local Computer > Personal. Re-enroll the agent.";
                state.Save();
                return Done(false, state.LastError, TimeSpan.FromMinutes(30));
            }

            using var client = new ServerClient(state.ServerUrl!, state.ServerPins, cert, state.ProxyUrl);
            var (host, os, _) = HostFacts();
            var resp = client.CheckinAsync(host, os, state.LastError, ct).GetAwaiter().GetResult();
            state.LastCheckinUtc = DateTime.UtcNow;
            if (resp.CheckinSeconds > 0) state.CheckinSeconds = Math.Max(60, resp.CheckinSeconds);
            if (resp.InventoryHours > 0) state.InventoryHours = resp.InventoryHours;
            MergePins(state, resp.ServerPins);

            if (resp.RenewCertificate) Renew(state, client, ct);
            if (resp.Actions is { Count: > 0 }) RunActions(client, resp.Actions, ct);

            state.LastError = null;
            var due = forceInventory || resp.InventoryDue || state.LastInventoryUtc is null || state.LastInventoryUtc < DateTime.UtcNow.AddHours(-state.InventoryHours);
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
            return Done(true, due ? "Checked in and sent inventory." : "Checked in.", Jitter(TimeSpan.FromSeconds(state.CheckinSeconds)));
        }
        catch (ServerException ex) when (ex.Status == HttpStatusCode.Forbidden && state.Enrolled)
        {
            Log.Warn("Server refused this agent: " + ex.Message);
            if (ex.Message.IndexOf("revoked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                CertificateStore.Remove(state.CertThumbprint);
                state.Status = "revoked";
                state.CertThumbprint = null;
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
            try { state.Save(); } catch (Exception) { }
            _failures++;
            // Back off 1, 2, 4 ... minutes, capped at the check-in interval (devices off the network retry quietly).
            var backoff = TimeSpan.FromMinutes(Math.Min(state.CheckinSeconds / 60.0, Math.Pow(2, Math.Min(_failures - 1, 10))));
            return Done(false, msg, Jitter(backoff));
        }
    }

    private static void Enroll(AgentState state, CancellationToken ct)
    {
        if (!AgentProtocol.TryParseCode(state.EnrollmentCode, out var code, out var error))
        {
            Log.Error("Enrollment code rejected: " + error);
            state.EnrollmentCode = null;
            state.LastError = error;
            state.Save();
            return;
        }
        var (host, os, uuid) = HostFacts();
        Log.Info($"Enrolling with {code!.Url} as {host}.");
        using var key = new PendingKey();
        using var client = new ServerClient(code.Url, code.Pins, null, state.ProxyUrl);
        EnrollResponse resp;
        try { resp = client.EnrollAsync(code.Token, key.CreateCsr(host), host, os, uuid, ct).GetAwaiter().GetResult(); }
        catch (ServerException ex) when (ex.Status == HttpStatusCode.Forbidden || ex.Status == HttpStatusCode.BadRequest)
        {
            // The code is invalid/expired/used up: drop it so the agent does not retry forever.
            Log.Error("Enrollment refused: " + ex.Message);
            state.EnrollmentCode = null;
            state.LastError = ex.Message;
            state.Save();
            return;
        }
        var installed = key.Install(Convert.FromBase64String(resp.Certificate));
        var old = state.CertThumbprint;
        state.AgentId = resp.AgentId;
        state.CertThumbprint = installed.Thumbprint;
        state.ServerUrl = code.Url;
        state.ServerPins = code.Pins.ToList();
        MergePins(state, resp.ServerPins);
        if (resp.CheckinSeconds > 0) state.CheckinSeconds = resp.CheckinSeconds;
        if (resp.InventoryHours > 0) state.InventoryHours = resp.InventoryHours;
        state.Status = "enrolled";
        state.EnrollmentCode = null;
        state.LastInventoryUtc = null;
        state.LastError = null;
        state.Save();
        if (!string.IsNullOrEmpty(old) && !string.Equals(old, installed.Thumbprint, StringComparison.OrdinalIgnoreCase)) CertificateStore.Remove(old);
        Log.Info($"Enrolled as agent {resp.AgentId}; certificate {installed.Thumbprint} expires {installed.NotAfter:yyyy-MM-dd}.");
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
    private static void RunActions(ServerClient client, System.Collections.Generic.List<AgentAction> actions, CancellationToken ct)
    {
        foreach (var a in actions)
        {
            bool ok; string? st; string msg;
            try { (ok, st, msg) = ServiceActions.Run(a.Kind, a.Target, a.Operation, TimeSpan.FromSeconds(60)); }
            catch (Exception ex) { ok = false; st = null; msg = ex.Message; }
            Log.Info($"Action {a.Operation} {a.Target}: {(ok ? "OK" : "FAILED")} - {msg}");
            try { client.ReportActionAsync(a.Id, ok, st, msg, ct).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Warn("Could not report action result: " + ex.Message); }
        }
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

    private static void MergePins(AgentState state, System.Collections.Generic.List<string>? pins)
    {
        if (pins is null || pins.Count == 0) return;
        // Keep the newest pins first; retain up to 4 so a server certificate change is picked up before the old one expires.
        state.ServerPins = pins.Concat(state.ServerPins).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Take(4).ToList();
    }

    private static string Describe(Exception ex)
    {
        var e = ex;
        while (e is AggregateException ae && ae.InnerException is not null) e = ae.InnerException;
        if (e is HttpRequestException hre && hre.InnerException is WebException we)
            return "Server unreachable: " + we.Message;
        return e.Message;
    }

    private static readonly Random Rng = new Random();
    private static TimeSpan Jitter(TimeSpan t) => TimeSpan.FromMilliseconds(t.TotalMilliseconds * (0.9 + Rng.NextDouble() * 0.2));
    private static Outcome Done(bool ok, string message, TimeSpan next) => new Outcome { Ok = ok, Message = message, NextRun = next };
}
