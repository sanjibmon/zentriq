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
    public string? ServerUrl { get; set; }
    public string? AgentId { get; set; }
    public string? CertThumbprint { get; set; }
    public List<string> ServerPins { get; set; } = new List<string>();
    public string? ProxyUrl { get; set; }
    public int CheckinSeconds { get; set; } = 900;
    public int InventoryHours { get; set; } = 12;
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
            ServerUrl = Str(k, "ServerUrl"),
            AgentId = Str(k, "AgentId"),
            CertThumbprint = Str(k, "CertThumbprint"),
            ProxyUrl = Str(k, "ProxyUrl"),
            Status = Str(k, "Status") ?? "pending",
            LastError = Str(k, "LastError"),
            ServerPins = (k.GetValue("ServerPins") as string[] ?? new string[0]).Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
            CheckinSeconds = Clamp(k.GetValue("CheckinSeconds") as int?, 60, 86400, 900),
            InventoryHours = Clamp(k.GetValue("InventoryHours") as int?, 1, 720, 12),
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
        k.SetValue("CheckinSeconds", CheckinSeconds, RegistryValueKind.DWord);
        k.SetValue("InventoryHours", InventoryHours, RegistryValueKind.DWord);
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
