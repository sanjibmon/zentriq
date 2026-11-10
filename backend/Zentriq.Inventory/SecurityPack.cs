using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Zentriq.Inventory;

/// <summary>
/// The Security Pack policy for one device (build 121), sent by the server and kept by the Windows agent so it applies
/// offline too: endpoint privilege management, BitLocker, USB device control.
/// </summary>
public sealed class SecurityPackPolicy
{
    [JsonPropertyName("revision")] public long Revision { get; set; }
    [JsonPropertyName("licensed")] public bool Licensed { get; set; }
    [JsonPropertyName("generatedAt")] public DateTimeOffset GeneratedAt { get; set; }
    [JsonPropertyName("privilege")] public PrivilegePolicy Privilege { get; set; } = new();
    [JsonPropertyName("bitlocker")] public BitLockerPolicy BitLocker { get; set; } = new();
    [JsonPropertyName("usb")] public UsbPolicy Usb { get; set; } = new();
}

public sealed class PrivilegePolicy
{
    /// <summary>off | audit (report administrators that would be removed) | enforce (remove them)</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "off";
    /// <summary>Accounts and groups that stay administrators (DOMAIN\name, name, SID; wildcards * ?).</summary>
    [JsonPropertyName("allowedAdmins")] public List<string> AllowedAdmins { get; set; } = new();
    /// <summary>"Run as administrator with Zentriq" for approved applications.</summary>
    [JsonPropertyName("elevation")] public bool Elevation { get; set; }
    /// <summary>Users may ask IT to approve an application or administrator rights for a while.</summary>
    [JsonPropertyName("requests")] public bool Requests { get; set; } = true;
    [JsonPropertyName("maxAdminMinutes")] public int MaxAdminMinutes { get; set; } = 240;
    [JsonPropertyName("rules")] public List<ElevationRule> Rules { get; set; } = new();
    [JsonPropertyName("grants")] public List<AdminGrant> Grants { get; set; } = new();
}

public sealed class ElevationRule
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>hash | publisher | path</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "hash";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("usesLeft")] public int? UsesLeft { get; set; }
}

public sealed class AdminGrant
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("until")] public DateTimeOffset Until { get; set; }
}

public sealed class BitLockerPolicy
{
    /// <summary>off | report (status and recovery keys only) | enforce (encrypt the system drive, keep a recovery password)</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "off";
    /// <summary>XtsAes256 | XtsAes128 | Aes256 | Aes128</summary>
    [JsonPropertyName("method")] public string Method { get; set; } = "XtsAes256";
    [JsonPropertyName("usedSpaceOnly")] public bool UsedSpaceOnly { get; set; } = true;
    /// <summary>Also encrypt fixed data drives (unlocked automatically with the system drive).</summary>
    [JsonPropertyName("fixedDrives")] public bool FixedDrives { get; set; }
    /// <summary>Replace a recovery password after it was shown in the console (one-time use).</summary>
    [JsonPropertyName("rotateAfterView")] public bool RotateAfterView { get; set; } = true;
    /// <summary>Recovery passwords (protector IDs) to replace now.</summary>
    [JsonPropertyName("rotate")] public List<string> Rotate { get; set; } = new();
}

public sealed class UsbPolicy
{
    /// <summary>off | audit (report devices and files) | readonly (removable storage read-only) | block (removable storage blocked)</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "off";
    /// <summary>Record files written, renamed and deleted on removable drives.</summary>
    [JsonPropertyName("auditFiles")] public bool AuditFiles { get; set; } = true;
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("allowed")] public List<UsbAllow> Allowed { get; set; } = new();
}

public sealed class UsbAllow
{
    /// <summary>VID_0781&amp;PID_5581, VID_0781&amp;PID_5581\SERIAL, or a device instance ID; wildcards * ?.</summary>
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    /// <summary>full | readonly</summary>
    [JsonPropertyName("access")] public string Access { get; set; } = "full";
    [JsonPropertyName("name")] public string? Name { get; set; }
}

/// <summary>Rules shared by the server (tests, previews) and the agent.</summary>
public static class SecurityPackRules
{
    private static readonly string[] PrivilegeOrder = { "off", "audit", "enforce" };
    private static readonly string[] BitLockerOrder = { "off", "report", "enforce" };
    private static readonly string[] UsbOrder = { "off", "audit", "readonly", "block" };

    private static string Strictest(string[] order, IEnumerable<string?> modes) =>
        modes.Where(m => m is not null && Array.IndexOf(order, m) >= 0).Select(m => m!).DefaultIfEmpty("off").OrderByDescending(m => Array.IndexOf(order, m)).First();

    public static string StrictestPrivilege(IEnumerable<string?> modes) => Strictest(PrivilegeOrder, modes);
    public static string StrictestBitLocker(IEnumerable<string?> modes) => Strictest(BitLockerOrder, modes);
    public static string StrictestUsb(IEnumerable<string?> modes) => Strictest(UsbOrder, modes);

    public static bool IsMode(string area, string? mode) => mode is not null && Array.IndexOf(area switch { "privilege" => PrivilegeOrder, "bitlocker" => BitLockerOrder, _ => UsbOrder }, mode) >= 0;

    /// <summary>Case-insensitive wildcard match (* and ?).</summary>
    public static bool Like(string? value, string? pattern)
    {
        if (value is null || string.IsNullOrEmpty(pattern)) return false;
        var rx = "^" + Regex.Escape(pattern!).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, rx, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    /// <summary>The rule that lets this file run as administrator, or null.</summary>
    public static ElevationRule? Match(IEnumerable<ElevationRule> rules, string path, string? sha256, string? publisher)
    {
        var name = path.Substring(path.LastIndexOfAny(new[] { '\\', '/' }) + 1);
        foreach (var r in rules)
        {
            if (r.UsesLeft is <= 0) continue;
            var ok = r.Kind switch
            {
                "hash" => sha256 is not null && string.Equals(r.Value.Trim(), sha256, StringComparison.OrdinalIgnoreCase),
                "publisher" => publisher is not null && Like(publisher, r.Value.Trim()) && (string.IsNullOrEmpty(r.FileName) || Like(name, r.FileName)),
                "path" => Like(path, r.Value.Trim()),
                _ => false,
            };
            if (ok) return r;
        }
        return null;
    }

    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>
    /// Whether a member of the local Administrators group stays: the built-in Administrator (RID 500), Domain Admins and
    /// Enterprise Admins (RID 512, 519), allowed accounts and groups, and users with time-limited rights now.
    /// </summary>
    public static string AdminStatus(string member, string? sid, PrivilegePolicy policy, DateTimeOffset now)
    {
        if (sid is not null && Regex.IsMatch(sid, @"^S-1-5-21-\d+-\d+-\d+-(500|512|519)$")) return "kept";
        var shortName = member.Substring(member.IndexOf('\\') + 1);
        if (policy.Grants.Any(g => g.Until > now && SameAccount(g.User, member))) return "granted";
        foreach (var a in policy.AllowedAdmins)
        {
            var p = a.Trim();
            if (p.Length == 0) continue;
            if ((sid is not null && string.Equals(p, sid, StringComparison.OrdinalIgnoreCase)) || Like(member, p) || (p.IndexOf('\\') < 0 && Like(shortName, p))) return "allowed";
        }
        return policy.Mode == "enforce" ? "removed" : "to_remove";
    }

    /// <summary>DOMAIN\user equals DOMAIN\user; a name without a domain equals the same name in any domain.</summary>
    public static bool SameAccount(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.IndexOf('\\') >= 0 && b.IndexOf('\\') >= 0) return false;
        return string.Equals(a.Substring(a.IndexOf('\\') + 1), b.Substring(b.IndexOf('\\') + 1), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Access of a USB storage device: full, readonly or blocked (allowed devices first, then the mode).</summary>
    public static string UsbAccess(UsbPolicy policy, string instanceId, string? vidPid, string? serial)
    {
        foreach (var a in policy.Allowed)
        {
            var id = a.DeviceId.Trim();
            if (id.Length == 0) continue;
            var hit = Like(instanceId, id)
                      || (vidPid is not null && (Like(vidPid, id) || (serial is not null && Like(vidPid + "\\" + serial, id))));
            if (hit) return a.Access == "readonly" ? "readonly" : "full";
        }
        return policy.Mode switch { "block" => "blocked", "readonly" => "readonly", _ => "full" };
    }

    /// <summary>VID_xxxx&amp;PID_yyyy and the serial number from a USB device instance ID (USB\VID_0781&amp;PID_5581\4C53…).</summary>
    public static (string? VidPid, string? Serial) ParseUsbId(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return (null, null);
        var m = Regex.Match(instanceId, @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:[^\\]*)\\([^\\]+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return (null, null);
        var serial = m.Groups[3].Value;
        // Windows makes up an instance (with '&') for devices without a serial number.
        return ($"VID_{m.Groups[1].Value.ToUpperInvariant()}&PID_{m.Groups[2].Value.ToUpperInvariant()}", serial.IndexOf('&') >= 0 ? null : serial);
    }
}

/// <summary>What the agent reports for the Security Pack (after applying the policy and with its events).</summary>
public sealed class SecurityPackReport
{
    [JsonPropertyName("revision")] public long Revision { get; set; }
    /// <summary>Null when the members of the Administrators group could not be read.</summary>
    [JsonPropertyName("admins")] public List<AdminMember>? Admins { get; set; }
    [JsonPropertyName("volumes")] public List<BitLockerVolume>? Volumes { get; set; }
    [JsonPropertyName("tpm")] public string? Tpm { get; set; }
    [JsonPropertyName("keys")] public List<RecoveryKey> Keys { get; set; } = new();
    [JsonPropertyName("usbDevices")] public List<UsbDevice> UsbDevices { get; set; } = new();
    [JsonPropertyName("privilegeEvents")] public List<PrivilegeEvent> PrivilegeEvents { get; set; } = new();
    [JsonPropertyName("usbEvents")] public List<UsbEvent> UsbEvents { get; set; } = new();
    /// <summary>Rules used for an elevation since the last report (each use counts once).</summary>
    [JsonPropertyName("usedRules")] public List<string> UsedRules { get; set; } = new();
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();
}

public sealed class AdminMember
{
    [JsonPropertyName("member")] public string Member { get; set; } = "";
    [JsonPropertyName("sid")] public string? Sid { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    /// <summary>kept | allowed | granted | removed | to_remove</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}

public sealed class BitLockerVolume
{
    [JsonPropertyName("volume")] public string Volume { get; set; } = "";
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("protection")] public string? Protection { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("percent")] public double? Percent { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }
    [JsonPropertyName("protectors")] public List<string> Protectors { get; set; } = new();
    /// <summary>IDs of the recovery passwords now on the volume (escrowed keys not listed are retired).</summary>
    [JsonPropertyName("recoveryIds")] public List<string> RecoveryIds { get; set; } = new();
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class RecoveryKey
{
    [JsonPropertyName("volume")] public string Volume { get; set; } = "";
    [JsonPropertyName("protectorId")] public string ProtectorId { get; set; } = "";
    [JsonPropertyName("password")] public string Password { get; set; } = "";
}

public sealed class UsbDevice
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("vidPid")] public string? VidPid { get; set; }
    [JsonPropertyName("serial")] public string? Serial { get; set; }
    [JsonPropertyName("access")] public string? Access { get; set; }
    [JsonPropertyName("seenAt")] public DateTimeOffset SeenAt { get; set; }
}

public sealed class PrivilegeEvent
{
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }
    [JsonPropertyName("ruleId")] public string? RuleId { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

public sealed class UsbEvent
{
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("deviceName")] public string? DeviceName { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

/// <summary>A user's request from the tray: run an application as administrator, or administrator rights for a while.</summary>
public sealed class PrivilegeRequest
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "elevate";
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }
    [JsonPropertyName("product")] public string? Product { get; set; }
    [JsonPropertyName("minutes")] public int? Minutes { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}
