using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>
/// The Security Pack on this device: the policy from the server (kept in C:\ProgramData\Zentriq\Agent\SecurityPack, so it
/// applies offline too), enforcing it — local administrators, time-limited administrator rights, the "Run as
/// administrator with Zentriq" command, BitLocker, USB storage — and the report with its events.
/// </summary>
internal static class SecurityPackAgent
{
    private static readonly object Gate = new object();
    private static SecurityPackPolicy? _policy;
    private static readonly List<PrivilegeEvent> PrivEvents = new List<PrivilegeEvent>();
    private static readonly List<UsbEvent> UsbEvents = new List<UsbEvent>();
    private static readonly List<string> UsedRules = new List<string>();
    private static readonly List<(DateTime At, string? User, string Text)> Notices = new List<(DateTime, string?, string)>();
    private static List<UsbDevice> _usbDevices = new List<UsbDevice>();
    private static DateTime _lastBitLocker = DateTime.MinValue, _lastAdmins = DateTime.MinValue;
    private static bool _reportDue = true;
    private static List<AdminMember>? _admins;
    private static (List<BitLockerVolume> Volumes, List<RecoveryKey> Keys, string Tpm)? _bitlocker;
    private static string? _bitlockerError;

    private static string Folder => Path.Combine(AgentPaths.Root, "SecurityPack");
    private static string PolicyFile => Path.Combine(Folder, "policy.json");
    private static string GrantsFile => Path.Combine(Folder, "granted.txt");      // accounts Zentriq made administrators: account|until (UTC ticks)
    private static string EscrowFile => Path.Combine(Folder, "escrowed.txt");     // recovery password IDs the server has

    public static SecurityPackPolicy Policy
    {
        get
        {
            lock (Gate)
            {
                if (_policy is null)
                {
                    try { if (File.Exists(PolicyFile)) _policy = JsonSerializer.Deserialize<SecurityPackPolicy>(File.ReadAllText(PolicyFile)); }
                    catch (Exception ex) { Log.Warn("Security Pack: the saved policy could not be read: " + ex.Message); }
                    _policy ??= new SecurityPackPolicy();
                }
                return _policy;
            }
        }
    }

    /// <summary>At every check-in: fetch a newer policy, apply it, send the report.</summary>
    public static void Sync(ServerClient client, SecurityPackInfo? info, CancellationToken ct)
    {
        if (info is null) return;
        try
        {
            if (info.Revision != Policy.Revision)
            {
                var text = client.SendJsonAsync(HttpMethod.Get, "security-pack/policy", null, ct).GetAwaiter().GetResult();
                var p = JsonSerializer.Deserialize<SecurityPackPolicy>(text) ?? new SecurityPackPolicy();
                Directory.CreateDirectory(Folder);
                File.WriteAllText(PolicyFile, text);
                lock (Gate) _policy = p;
                Log.Info($"Security Pack policy {p.Revision}: privilege {p.Privilege.Mode}, BitLocker {p.BitLocker.Mode}, USB {p.Usb.Mode}" + (p.Licensed ? "" : " (not licensed: off)"));
                Apply(force: true);
            }
            if (_reportDue || PendingCount() > 0) Report(client, ct);
        }
        catch (Exception ex) { Log.Warn("Security Pack: " + ex.Message); }
    }

    private static int PendingCount() { lock (Gate) return PrivEvents.Count + UsbEvents.Count + UsedRules.Count; }

    private static void Report(ServerClient client, CancellationToken ct)
    {
        SecurityPackReport r;
        lock (Gate)
        {
            r = new SecurityPackReport
            {
                Revision = Policy.Revision, Admins = _admins, PrivilegeEvents = PrivEvents.ToList(), UsbEvents = UsbEvents.ToList(), UsedRules = UsedRules.ToList(), UsbDevices = _usbDevices.ToList(),
            };
            if (_bitlocker is { } b)
            {
                r.Volumes = b.Volumes;
                r.Tpm = b.Tpm;
                var sent = Escrowed();
                r.Keys = b.Keys.Where(k => !sent.Contains(k.ProtectorId)).ToList();
            }
            if (_bitlockerError is not null) r.Errors.Add(_bitlockerError);
        }
        client.SendJsonAsync(HttpMethod.Post, "security-pack/report", r, ct).GetAwaiter().GetResult();
        lock (Gate)
        {
            PrivEvents.RemoveRange(0, Math.Min(PrivEvents.Count, r.PrivilegeEvents.Count));
            UsbEvents.RemoveRange(0, Math.Min(UsbEvents.Count, r.UsbEvents.Count));
            UsedRules.RemoveRange(0, Math.Min(UsedRules.Count, r.UsedRules.Count));
            if (r.Keys.Count > 0) { Directory.CreateDirectory(Folder); File.AppendAllLines(EscrowFile, r.Keys.Select(k => k.ProtectorId)); }
            _reportDue = false;
        }
    }

    private static HashSet<string> Escrowed()
    {
        try { return new HashSet<string>(File.Exists(EscrowFile) ? File.ReadAllLines(EscrowFile) : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase); }
        catch (Exception) { return new HashSet<string>(); }
    }

    private static void Event(PrivilegeEvent e) { lock (Gate) { PrivEvents.Add(e); if (PrivEvents.Count > 1000) PrivEvents.RemoveAt(0); } }
    private static void Event(UsbEvent e) { lock (Gate) { UsbEvents.Add(e); if (UsbEvents.Count > 5000) UsbEvents.RemoveAt(0); } }

    internal static void Notify(string? user, string text) { lock (Gate) { Notices.Add((DateTime.UtcNow, user, text)); if (Notices.Count > 50) Notices.RemoveAt(0); } }

    /// <summary>Messages for the tray of this user since <paramref name="since"/>.</summary>
    internal static List<string> NoticesFor(string? user, DateTime since)
    {
        lock (Gate) return Notices.Where(n => n.At > since && (n.User is null || user is not null && SecurityPackRules.SameAccount(n.User, user))).Select(n => n.Text).ToList();
    }

    /// <summary>Every few seconds: USB storage; every minute: administrators and rights for a while; every 30 minutes: BitLocker.</summary>
    public static void Loop(CancellationToken ct)
    {
        Apply(force: true);
        var tick = 0;
        while (!ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(3)))
        {
            tick++;
            try
            {
                var p = Policy;
                if ((p.Licensed && p.Usb.Mode != "off") || UsbControl.HasRestrictions)
                {
                    var before = PendingCount();
                    var devices = UsbControl.Check(p.Licensed ? p.Usb : new UsbPolicy(), Event, text => Notify(null, text));
                    lock (Gate) _usbDevices = devices;
                    if (PendingCount() > before) AgentService.RequestServerWake();
                }
                if (tick % 20 == 0) Apply(force: false);
            }
            catch (Exception ex) { Log.Warn("Security Pack: " + ex.Message); }
        }
    }

    private static readonly object ApplyGate = new object();

    public static void Apply(bool force)
    {
        if (!UninstallProtection.CanManage) return;   // only the service (SYSTEM) changes the device
        lock (ApplyGate)
        {
            var p = Policy;
            var before = PendingCount();
            try { ApplyPrivilege(p, force); } catch (Exception ex) { Log.Warn("Security Pack (administrators): " + ex.Message); }
            try { ShellVerb(p.Licensed && p.Privilege.Elevation); } catch (Exception ex) { Log.Warn("Security Pack (Explorer command): " + ex.Message); }
            if (force || DateTime.UtcNow - _lastBitLocker > TimeSpan.FromMinutes(30))
            {
                _lastBitLocker = DateTime.UtcNow;
                if (p.Licensed && p.BitLocker.Mode != "off")
                {
                    try
                    {
                        var (volumes, keys, tpm, log) = BitLockerAgent.Apply(p.BitLocker);
                        foreach (var l in log) Log.Info("BitLocker: " + l);
                        var old = _bitlocker;
                        lock (Gate) { _bitlocker = (volumes, keys, tpm); _bitlockerError = null; }
                        if (old is null || log.Count > 0 || Sig(old.Value.Volumes) != Sig(volumes) || keys.Any(k => !Escrowed().Contains(k.ProtectorId))) _reportDue = true;
                    }
                    catch (Exception ex) { lock (Gate) { _bitlockerError = ex.Message; _reportDue = true; } }
                }
            }
            if (PendingCount() > before || _reportDue) AgentService.RequestServerWake();
        }
    }

    private static string Sig(List<BitLockerVolume> v) => string.Join(";", v.Select(x => $"{x.Volume}:{x.Protection}:{x.Status}:{(int)(x.Percent ?? 0) / 10}:{string.Join(",", x.RecoveryIds)}:{x.Error}"));

    // ---- administrators -------------------------------------------------------------------------------------------

    private static Dictionary<string, DateTime> Granted()
    {
        var d = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(GrantsFile))
                foreach (var line in File.ReadAllLines(GrantsFile))
                {
                    var i = line.LastIndexOf('|');
                    if (i > 0 && long.TryParse(line.Substring(i + 1), out var t)) d[line.Substring(0, i)] = new DateTime(t, DateTimeKind.Utc);
                }
        }
        catch (Exception) { }
        return d;
    }

    private static void SaveGranted(Dictionary<string, DateTime> d)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllLines(GrantsFile, d.Select(kv => kv.Key + "|" + kv.Value.Ticks));
    }

    private static void ApplyPrivilege(SecurityPackPolicy p, bool force)
    {
        var now = DateTimeOffset.UtcNow;
        var priv = p.Licensed ? p.Privilege : new PrivilegePolicy();
        // Time-limited administrator rights: add, and remove when they end (also offline, from the saved end time).
        var granted = Granted();
        var changed = false;
        foreach (var g in priv.Grants.Where(g => g.Until > now))
        {
            if (granted.TryGetValue(g.User, out var until) && until == g.Until.UtcDateTime) continue;
            try
            {
                // Already an administrator (not through Zentriq): nothing to add, and nothing to take away later.
                if (!granted.ContainsKey(g.User) && Privilege.Members().Any(m => SecurityPackRules.SameAccount(m.Name, g.User))) continue;
                Privilege.Add(g.User);
                granted[g.User] = g.Until.UtcDateTime;
                changed = true;
                Event(new PrivilegeEvent { At = now, Kind = "admin_added", User = g.User, Detail = $"Administrator until {g.Until.ToLocalTime():g}" });
                Notify(g.User, $"You are an administrator on this computer until {g.Until.ToLocalTime():t}. Sign out and in again, or use Run as administrator.");
            }
            catch (Exception ex) { Event(new PrivilegeEvent { At = now, Kind = "failed", User = g.User, Detail = "Adding administrator rights failed: " + ex.Message }); }
        }
        foreach (var kv in granted.ToList())
        {
            var active = priv.Grants.Any(g => g.Until > now && SecurityPackRules.SameAccount(g.User, kv.Key));
            if (active && kv.Value > DateTime.UtcNow) continue;
            try
            {
                var member = Privilege.Members().FirstOrDefault(m => SecurityPackRules.SameAccount(m.Name, kv.Key));
                if (member?.Sid is { } sid) Privilege.Remove(sid);
                Event(new PrivilegeEvent { At = now, Kind = "admin_removed", User = kv.Key, Detail = kv.Value <= DateTime.UtcNow ? "Time ended" : "Revoked" });
                Notify(kv.Key, "Your administrator rights on this computer have ended.");
            }
            catch (Exception ex) { Log.Warn($"Security Pack: removing administrator rights of {kv.Key} failed: {ex.Message}"); continue; }
            granted.Remove(kv.Key);
            changed = true;
        }
        if (changed) SaveGranted(granted);

        if (priv.Mode == "off") { if (_admins is not null) { _admins = null; } return; }
        if (!force && !changed && DateTime.UtcNow - _lastAdmins < TimeSpan.FromMinutes(5)) return;
        _lastAdmins = DateTime.UtcNow;
        var members = Privilege.Members();
        var list = new List<AdminMember>();
        var keepSafe = priv.Mode == "enforce" && !Privilege.DomainJoined() && Privilege.BuiltInAdministratorDisabled() && priv.AllowedAdmins.Count == 0;
        foreach (var m in members)
        {
            var status = SecurityPackRules.AdminStatus(m.Name, m.Sid, priv, now);
            if (m.Sid == "S-1-5-18") status = "kept";
            if (status == "removed" && keepSafe) status = "to_remove";
            if (status == "removed")
            {
                try
                {
                    Privilege.Remove(m.Sid ?? throw new InvalidOperationException("no SID"));
                    Event(new PrivilegeEvent { At = now, Kind = "member_removed", User = m.Name, Detail = "Removed from the local Administrators group" });
                }
                catch (Exception ex) { status = "to_remove"; Event(new PrivilegeEvent { At = now, Kind = "failed", User = m.Name, Detail = "Removing from Administrators failed: " + ex.Message }); }
            }
            list.Add(new AdminMember { Member = m.Name, Sid = m.Sid, Kind = m.Kind, Status = status });
        }
        if (keepSafe) Log.Warn("Security Pack: administrators are not removed — this computer has no domain and its built-in Administrator is disabled; add an allowed account to the policy.");
        var sig = string.Join(";", list.Select(x => x.Member + "=" + x.Status));
        if (_admins is null || string.Join(";", _admins.Select(x => x.Member + "=" + x.Status)) != sig) _reportDue = true;
        lock (Gate) _admins = list;
    }

    /// <summary>"Run as administrator with Zentriq" in Explorer's menu for programs and Windows Installer packages.</summary>
    private static void ShellVerb(bool on)
    {
        var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZentriqAgent.exe");
        foreach (var cls in new[] { "exefile", "Msi.Package" })
        {
            var path = $@"SOFTWARE\Classes\{cls}\shell\ZentriqElevate";
            if (!on) { Registry.LocalMachine.DeleteSubKeyTree(path, false); continue; }
            using var k = Registry.LocalMachine.CreateSubKey(path);
            k.SetValue("MUIVerb", "Run as administrator with Zentriq");
            k.SetValue("Icon", exe + ",0");
            using var c = k.CreateSubKey("command");
            c.SetValue("", $"\"{exe}\" --elevate \"%1\"");
        }
    }

    // ---- elevation ------------------------------------------------------------------------------------------------

    /// <summary>A user asked to run a program as administrator (tray or Explorer). Started when a rule allows it.</summary>
    public static Dictionary<string, object?> Elevate(int session, string? user, string? path, string? arguments)
    {
        var p = Policy;
        if (!p.Licensed || !p.Privilege.Elevation) return new() { ["state"] = "off", ["message"] = "Running programs as administrator is not turned on for this computer." };
        if (user is null || session <= 0) return new() { ["state"] = "failed", ["message"] = "No signed-in user." };
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path!.StartsWith(@"\\", StringComparison.Ordinal)) return new() { ["state"] = "failed", ["message"] = "Choose a program on this computer." };
        var full = Path.GetFullPath(path);
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext is not (".exe" or ".msi")) return new() { ["state"] = "failed", ["message"] = "Only programs (.exe) and Windows Installer packages (.msi) can be run as administrator." };
        // Held open (read only for others) from the check until the program has started, so it cannot be swapped in between.
        using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sha = Privilege.Sha256(file);
        var (cn, o) = Privilege.Signer(full);
        var rule = SecurityPackRules.Match(p.Privilege.Rules, full, sha, cn) ?? (o is null ? null : SecurityPackRules.Match(p.Privilege.Rules, full, sha, o));
        var info = new Dictionary<string, object?> { ["sha256"] = sha, ["publisher"] = cn ?? o, ["path"] = full, ["requests"] = p.Privilege.Requests };
        if (rule is null)
        {
            Event(new PrivilegeEvent { At = DateTimeOffset.UtcNow, Kind = "refused", User = user, Path = full, Sha256 = sha, Publisher = cn ?? o, Detail = "Not approved" });
            AgentService.RequestServerWake();
            info["state"] = "not_approved";
            info["message"] = $"{Path.GetFileName(full)} is not approved to run as administrator." + (p.Privilege.Requests ? " You can ask IT to approve it." : "");
            return info;
        }
        try
        {
            var cmd = ext == ".msi" ? $"\"{Path.Combine(Environment.SystemDirectory, "msiexec.exe")}\" /i \"{full}\"" : $"\"{full}\"" + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments);
            var pid = Privilege.StartElevated(session, user, cmd, Path.GetDirectoryName(full));
            if (rule.UsesLeft is { } left) { rule.UsesLeft = left - 1; lock (Gate) UsedRules.Add(rule.Id); }
            Event(new PrivilegeEvent { At = DateTimeOffset.UtcNow, Kind = "elevated", User = user, Path = full, Sha256 = sha, Publisher = cn ?? o, RuleId = rule.Id, Detail = $"Started as administrator (process {pid}, rule {rule.Name})" });
            AgentService.RequestServerWake();
            info["state"] = "started";
            info["pid"] = pid;
            info["message"] = $"{Path.GetFileName(full)} started as administrator.";
            return info;
        }
        catch (Exception ex)
        {
            Event(new PrivilegeEvent { At = DateTimeOffset.UtcNow, Kind = "failed", User = user, Path = full, Sha256 = sha, Publisher = cn ?? o, RuleId = rule.Id, Detail = ex.Message });
            AgentService.RequestServerWake();
            info["state"] = "failed";
            info["message"] = "Could not start it as administrator: " + ex.Message;
            return info;
        }
    }

    /// <summary>The facts of a program for a request (hash, signer, product), read by the service.</summary>
    public static Dictionary<string, object?> Describe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("The program was not found.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var (cn, o) = Privilege.Signer(path!);
        string? product = null;
        try { var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path!); product = string.IsNullOrWhiteSpace(v.ProductName) ? v.FileDescription : v.ProductName; } catch (Exception) { }
        return new Dictionary<string, object?> { ["path"] = Path.GetFullPath(path!), ["sha256"] = Privilege.Sha256(file), ["publisher"] = cn ?? o, ["product"] = product };
    }

    /// <summary>What the tray shows for this user.</summary>
    public static Dictionary<string, object?> Status(string? user)
    {
        var p = Policy;
        var grant = user is null ? null : p.Privilege.Grants.Where(g => g.Until > DateTimeOffset.UtcNow && SecurityPackRules.SameAccount(g.User, user)).OrderByDescending(g => g.Until).FirstOrDefault();
        return new Dictionary<string, object?>
        {
            ["elevation"] = p.Licensed && p.Privilege.Elevation, ["requests"] = p.Licensed && p.Privilege.Requests,
            ["adminRequests"] = p.Licensed && p.Privilege.Requests, ["maxAdminMinutes"] = p.Privilege.MaxAdminMinutes,
            ["adminUntil"] = grant?.Until.ToLocalTime().ToString("t"),
        };
    }
}

internal sealed class SecurityPackInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("revision")] public long Revision { get; set; }
}
