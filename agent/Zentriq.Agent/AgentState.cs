using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Zentriq.Agent;

/// <summary>
/// Persistent agent state in HKLM\SOFTWARE\Zentriq\Agent (64-bit view). The key is restricted to SYSTEM and
/// Administrators. The enrollment code (written by the MSI) is deleted once enrollment succeeds.
/// </summary>
internal sealed class AgentState
{
    public const string KeyPath = @"SOFTWARE\Zentriq\Agent";

    public string? EnrollmentCode { get; set; }
    /// <summary>Server for a short (20-character) enrollment code, as entered at installation (SERVERURL).</summary>
    public string? EnrollmentServer { get; set; }
    public string? ServerUrl { get; set; }
    /// <summary>Other addresses of the server (its names and IP addresses), tried when <see cref="ServerUrl"/> does not answer.</summary>
    public List<string> ServerUrls { get; set; } = new List<string>();
    public string? AgentId { get; set; }
    public string? CertThumbprint { get; set; }
    public List<string> ServerPins { get; set; } = new List<string>();
    public string? ProxyUrl { get; set; }
    /// <summary>How often the agent checks its connection to the server (and picks up console requests), in minutes.</summary>
    public int CheckinMinutes { get; set; } = 30;
    /// <summary>How often the agent sends its inventory, in minutes.</summary>
    public int InventoryMinutes { get; set; } = 180;
    /// <summary>Intervals chosen at installation (sent to the server when enrolling), or null.</summary>
    public int? InstallCheckinMinutes { get; set; }
    public int? InstallInventoryMinutes { get; set; }
    /// <summary>Set when the server stopped answering: the last time it was reached. Reported once it answers again.</summary>
    public DateTime? OfflineSinceUtc { get; set; }
    public DateTime? LastInventoryUtc { get; set; }
    public DateTime? LastCheckinUtc { get; set; }
    /// <summary>pending | enrolled | revoked</summary>
    public string Status { get; set; } = "pending";
    public string? LastError { get; set; }

    public bool Enrolled => Status == "enrolled" && !string.IsNullOrEmpty(AgentId) && !string.IsNullOrEmpty(CertThumbprint);

    private static RegistryKey Open(bool write)
    {
        var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return write ? hklm.CreateSubKey(KeyPath, true) : (hklm.OpenSubKey(KeyPath, false) ?? hklm.CreateSubKey(KeyPath, true));
    }

    public static AgentState Load()
    {
        using var k = Open(false);
        var s = new AgentState
        {
            EnrollmentCode = Str(k, "EnrollmentCode"),
            EnrollmentServer = Str(k, "EnrollmentServer"),
            ServerUrl = Str(k, "ServerUrl"),
            ServerUrls = (k.GetValue("ServerUrls") as string[] ?? new string[0]).Where(u => !string.IsNullOrWhiteSpace(u)).ToList(),
            AgentId = Str(k, "AgentId"),
            CertThumbprint = Str(k, "CertThumbprint"),
            ProxyUrl = Str(k, "ProxyUrl"),
            Status = Str(k, "Status") ?? "pending",
            LastError = Str(k, "LastError"),
            ServerPins = (k.GetValue("ServerPins") as string[] ?? new string[0]).Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
            CheckinMinutes = Clamp(k.GetValue("CheckinMinutes") as int?, 2, 1440, 30),
            InventoryMinutes = Clamp(k.GetValue("InventoryMinutes") as int?, 30, 10080, 180),
            InstallCheckinMinutes = k.GetValue("InstallCheckinMinutes") is int ic && ic >= 2 && ic <= 1440 ? ic : (int?)null,
            InstallInventoryMinutes = k.GetValue("InstallInventoryMinutes") is int ii && ii >= 30 && ii <= 10080 ? ii : (int?)null,
            OfflineSinceUtc = Date(Str(k, "OfflineSinceUtc")),
            LastInventoryUtc = Date(Str(k, "LastInventoryUtc")),
            LastCheckinUtc = Date(Str(k, "LastCheckinUtc")),
        };
        return s;
    }

    public void Save()
    {
        using var k = Open(true);
        Set(k, "ServerUrl", ServerUrl);
        Set(k, "AgentId", AgentId);
        Set(k, "CertThumbprint", CertThumbprint);
        Set(k, "Status", Status);
        Set(k, "LastError", LastError);
        Set(k, "LastInventoryUtc", LastInventoryUtc?.ToString("o"));
        Set(k, "LastCheckinUtc", LastCheckinUtc?.ToString("o"));
        k.SetValue("ServerPins", ServerPins.ToArray(), RegistryValueKind.MultiString);
        k.SetValue("ServerUrls", ServerUrls.ToArray(), RegistryValueKind.MultiString);
        k.SetValue("CheckinMinutes", CheckinMinutes, RegistryValueKind.DWord);
        k.SetValue("InventoryMinutes", InventoryMinutes, RegistryValueKind.DWord);
        Set(k, "OfflineSinceUtc", OfflineSinceUtc?.ToString("o"));
        Set(k, "EnrollmentServer", EnrollmentServer);
        if (string.IsNullOrEmpty(EnrollmentCode)) { try { k.DeleteValue("EnrollmentCode", false); } catch (UnauthorizedAccessException) { } }
        else k.SetValue("EnrollmentCode", EnrollmentCode, RegistryValueKind.String);
    }

    /// <summary>SYSTEM and Administrators only (the enrollment code is a secret until used).</summary>
    public static void Secure()
    {
        try
        {
            using var k = Open(true);
            var sec = new RegistrySecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            k.SetAccessControl(sec);
        }
        catch (Exception ex) { Log.Warn("Could not restrict the agent registry key: " + ex.Message); }
    }

    private static string? Str(RegistryKey k, string name) => k.GetValue(name) is string s && s.Trim().Length > 0 ? s.Trim() : null;
    private static void Set(RegistryKey k, string name, string? value)
    {
        if (value is null) { k.DeleteValue(name, false); return; }
        k.SetValue(name, value, RegistryValueKind.String);
    }
    private static int Clamp(int? v, int min, int max, int fallback) => v is int i ? Math.Max(min, Math.Min(max, i)) : fallback;
    private static DateTime? Date(string? s) => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : (DateTime?)null;
}

/// <summary>
/// Uninstall protection: the device's uninstall key (from the server) in HKLM\SOFTWARE\Zentriq\Agent\Protection, readable by
/// SYSTEM only (not by Administrators), so a code can only come from the console. The Windows Installer package checks a
/// typed code against it as SYSTEM. "UninstallProtected" in the agent key (readable by administrators) tells the installer
/// that a code is needed.
/// </summary>
internal static class UninstallProtection
{
    public const string KeyPath = AgentState.KeyPath + @"\Protection";

    public static byte[]? Key()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(KeyPath, false);
            return k?.GetValue("UninstallKey") as byte[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException) { return null; } // not SYSTEM
    }

    /// <summary>Protection is managed by the agent service (SYSTEM) only, not by commands an administrator runs.</summary>
    public static bool CanManage => WindowsIdentity.GetCurrent().IsSystem;

    public static bool Enabled => Key() is { Length: 32 };

    /// <summary>
    /// The uninstall code for all agents (hash and expiry, SYSTEM-only like the key); checked by the agent package when
    /// someone removes the agent. Null removes it.
    /// </summary>
    /// <summary>Hash of the uninstall code this device holds ("" none); null when it cannot be read (not SYSTEM).</summary>
    public static string? CodeForAllHash()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(KeyPath, false);
            if (k is null) return "";
            if (!(k.GetValue("AllCodeExpires") is string e) || !DateTime.TryParse(e, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var until) || until < DateTime.UtcNow)
                return "";   // none, or expired (the server then sends no code; the stale values are removed)
            return k.GetValue("AllCodeHash") as string ?? "";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException) { return null; }
    }

    public static void ApplyCodeForAll(UninstallCodeInfo? code)
    {
        if (!Enabled) return;
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey(KeyPath, true);
        if (k is null) return;
        var hash = code?.Hash?.Trim().ToLowerInvariant();
        var expires = code?.ExpiresAt.UtcDateTime.ToString("o");
        if (string.IsNullOrEmpty(hash) || hash!.Length != 64)
        {
            if (k.GetValue("AllCodeHash") is null) return;
            k.DeleteValue("AllCodeHash", false);
            k.DeleteValue("AllCodeExpires", false);
            Log.Info("Uninstall code removed (expired or cancelled in the console).");
            return;
        }
        if (k.GetValue("AllCodeHash") as string == hash && k.GetValue("AllCodeExpires") as string == expires) return;
        k.SetValue("AllCodeHash", hash, RegistryValueKind.String);
        k.SetValue("AllCodeExpires", expires!, RegistryValueKind.String);
        Log.Info($"New uninstall code from the console received (valid until {code!.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC).");
    }

    /// <summary>Stores or removes the key; returns true when something changed.</summary>
    public static bool Apply(byte[]? key)
    {
        var current = Key();
        if (key is null)
        {
            if (current is null) return false;
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            hklm.DeleteSubKeyTree(KeyPath, false);
            using (var a = hklm.CreateSubKey(AgentState.KeyPath, true)) a.DeleteValue("UninstallProtected", false);
            ServiceHardening.Apply(false);
            Log.Info("Uninstall protection turned off by the server.");
            return true;
        }
        if (current is not null && current.SequenceEqual(key)) { ServiceHardening.Apply(true); return false; }
        var sec = new RegistrySecurity();
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        {
            using (var k = hklm.CreateSubKey(KeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, sec))
            {
                k.SetAccessControl(sec);
                k.SetValue("UninstallKey", key, RegistryValueKind.Binary);
            }
            using (var a = hklm.CreateSubKey(AgentState.KeyPath, true)) a.SetValue("UninstallProtected", 1, RegistryValueKind.DWord);
        }
        ServiceHardening.Apply(true);
        Log.Info("Uninstall protection is on: removing the agent needs a one-hour code from the Zentriq console.");
        return true;
    }
}

/// <summary>
/// While uninstall protection is on, administrators can query and start the agent service but not stop, reconfigure or
/// delete it (Windows Installer, running as SYSTEM, still can, so updates keep working).
/// </summary>
internal static class ServiceHardening
{
    private const string Default = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
    private const string Hardened = "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWRPLOCRRC;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
    private static bool? _applied;

    public static void Apply(bool hardened)
    {
        if (_applied == hardened) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "sc.exe"), $"sdset {Program.ServiceName} {(hardened ? Hardened : Default)}")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(15000);
            if (p.ExitCode == 0) _applied = hardened;
            else Log.Warn($"Could not {(hardened ? "protect" : "unprotect")} the agent service (sc sdset exit {p.ExitCode}).");
        }
        catch (Exception ex) { Log.Warn("Could not change the agent service permissions: " + ex.Message); }
    }
}
