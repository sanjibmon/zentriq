using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>
/// Application control on this device: the cached policy from the server (approved software, mode, message, WDAC,
/// remote support protection), the events and requests waiting to be sent, and turning the installer watch, WDAC and
/// the remote support agent's protection on or off when the policy changes. The policy is kept in
/// C:\ProgramData\Zentriq\Agent\AppControl (SYSTEM and administrators only), so it applies while the device is offline.
/// </summary>
internal static class AppControlAgent
{
    private static readonly object Gate = new object();
    private static AppControlPolicy? _policy;
    private static long _seenRevision = -1;
    private static readonly List<Dictionary<string, object?>> Events = new List<Dictionary<string, object?>>();
    private static readonly List<Dictionary<string, object?>> Requests = new List<Dictionary<string, object?>>();
    private static readonly Dictionary<string, DateTime> Recent = new Dictionary<string, DateTime>();
    private static string Folder => Path.Combine(AgentPaths.Root, "AppControl");
    private static string PolicyFile => Path.Combine(Folder, "policy.json");
    private static string QueueFile => Path.Combine(Folder, "queue.json");

    public static AppControlPolicy Policy
    {
        get
        {
            lock (Gate)
            {
                if (_policy is null) Load();
                return _policy ?? new AppControlPolicy();
            }
        }
    }

    private static void Load()
    {
        try
        {
            if (File.Exists(PolicyFile)) _policy = JsonSerializer.Deserialize<AppControlPolicy>(File.ReadAllText(PolicyFile));
            if (File.Exists(QueueFile))
            {
                var q = JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, object?>>>>(File.ReadAllText(QueueFile));
                if (q is not null)
                {
                    if (q.TryGetValue("events", out var e)) Events.AddRange(e);
                    if (q.TryGetValue("requests", out var r)) Requests.AddRange(r);
                }
            }
        }
        catch (Exception ex) { Log.Warn("Application control: could not read the saved policy: " + ex.Message); }
        _policy ??= new AppControlPolicy();
        _seenRevision = _policy.Revision;
    }

    private static void SaveQueue()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(QueueFile, JsonSerializer.Serialize(new Dictionary<string, object> { ["events"] = Events, ["requests"] = Requests }));
        }
        catch (Exception ex) { Log.Warn("Application control: could not save the queue: " + ex.Message); }
    }

    /// <summary>Called when the service starts: applies the saved policy (offline too).</summary>
    public static void OnServiceStart()
    {
        if (!UninstallProtection.CanManage) return;
        try { Apply(Policy, startup: true); } catch (Exception ex) { Log.Warn("Application control: " + ex.Message); }
    }

    /// <summary>At every check-in: fetch the policy when the server has a newer one, send events and requests.</summary>
    public static void Sync(ServerClient client, AppControlInfo? info, CancellationToken ct)
    {
        if (!UninstallProtection.CanManage) return;
        if (info is not null && info.Revision != Policy.Revision)
        {
            lock (Gate) _seenRevision = info.Revision;
            try
            {
                var json = client.GetAppControlPolicyAsync(ct).GetAwaiter().GetResult();
                var p = JsonSerializer.Deserialize<AppControlPolicy>(json) ?? new AppControlPolicy();
                Directory.CreateDirectory(Folder);
                File.WriteAllText(PolicyFile, json);
                var old = Policy;
                lock (Gate) _policy = p;
                Log.Info($"Application control policy {p.Revision}: {p.Mode}, {p.Rules.Count} rule(s), WDAC {p.Wdac}" + (old.Mode != p.Mode ? $" (was {old.Mode})" : "") + ".");
                Apply(p, startup: false);
                AgentService.RequestServerWake();   // report the applied policy at once (the console shows it as current)
            }
            catch (Exception ex) { Log.Warn("Application control: could not fetch the policy: " + AgentWorker.Describe(ex)); }
        }
        Flush(client, ct);
    }

    private static void Apply(AppControlPolicy p, bool startup)
    {
        if (p.Mode == "off") InstallerWatch.Stop(); else InstallerWatch.Start();
        Wdac.Apply(p.Wdac, startup);
        RemoteGuard.Sync();
    }

    /// <summary>What the check-in reports about application control on this device.</summary>
    public static Dictionary<string, object?>? Report()
    {
        if (!UninstallProtection.CanManage) return null;
        long revision;
        lock (Gate) { if (_policy is null) Load(); revision = Math.Max(_seenRevision, _policy!.Revision); }
        return new Dictionary<string, object?>
        {
            ["revision"] = revision,
            ["mode"] = InstallerWatch.Running ? Policy.Mode : Policy.Mode == "off" ? "off" : "error",
            ["wdac"] = Wdac.State,
            ["remoteProtection"] = RemoteGuard.State,
        };
    }

    public static void AddEvent(string kind, SoftwareCandidate c, string? user, string detail)
    {
        lock (Gate)
        {
            if (_policy is null) Load();
            // The same software on the same device is reported at most once in 10 minutes.
            var key = kind + "|" + c.Name + "|" + c.Version;
            if (Recent.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(10)) return;
            Recent[key] = DateTime.UtcNow;
            if (Recent.Count > 200) foreach (var old in Recent.Where(kv => DateTime.UtcNow - kv.Value > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList()) Recent.Remove(old);
            Events.Add(new Dictionary<string, object?>
            {
                ["key"] = key, ["kind"] = kind, ["at"] = DateTime.UtcNow.ToString("o"), ["name"] = c.Name, ["publisher"] = c.Publisher ?? c.Signer, ["version"] = c.Version,
                ["sha256"] = c.Sha256, ["path"] = c.Path, ["user"] = user, ["detail"] = detail,
            });
            if (Events.Count > 500) Events.RemoveRange(0, Events.Count - 500);
            SaveQueue();
        }
        AgentService.RequestServerWake();
    }

    public static void AddRequest(SoftwareCandidate c, string? user, string? reason)
    {
        lock (Gate)
        {
            if (_policy is null) Load();
            Requests.Add(new Dictionary<string, object?>
            {
                ["name"] = c.Name, ["publisher"] = c.Publisher ?? c.Signer, ["version"] = c.Version, ["sha256"] = c.Sha256, ["path"] = c.Path, ["user"] = user, ["reason"] = reason,
            });
            if (Requests.Count > 50) Requests.RemoveRange(0, Requests.Count - 50);
            SaveQueue();
        }
        Log.Info($"Application control: {user ?? "a user"} asked for approval of {c}.");
        AgentService.RequestServerWake();
    }

    private static void Flush(ServerClient client, CancellationToken ct)
    {
        List<Dictionary<string, object?>> events, requests;
        lock (Gate) { events = Events.ToList(); requests = Requests.ToList(); }
        if (events.Count > 0)
            try
            {
                client.PostAppControlEventsAsync(events.Select(e => e.Where(kv => kv.Key != "key").ToDictionary(kv => kv.Key, kv => kv.Value)).ToList(), ct).GetAwaiter().GetResult();
                lock (Gate) { Events.RemoveAll(e => events.Contains(e)); SaveQueue(); }
            }
            catch (Exception ex) { Log.Warn("Application control: could not send events: " + AgentWorker.Describe(ex)); }
        foreach (var r in requests)
            try
            {
                client.PostAppControlRequestAsync(r, ct).GetAwaiter().GetResult();
                lock (Gate) { Requests.Remove(r); SaveQueue(); }
            }
            catch (ServerException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden || ex.Status == System.Net.HttpStatusCode.BadRequest)
            {
                Log.Warn("Application control: request not accepted: " + ex.Message);
                lock (Gate) { Requests.Remove(r); SaveQueue(); }
            }
            catch (Exception ex) { Log.Warn("Application control: could not send the request: " + AgentWorker.Describe(ex)); break; }
    }

    /// <summary>Decides on software about to be installed (the installer watch).</summary>
    public static AppDecision Evaluate(SoftwareCandidate c) => AppControl.Evaluate(Policy, c);
}
