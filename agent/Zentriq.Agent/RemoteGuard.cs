using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Zentriq.Agent;

/// <summary>
/// Tamper protection for the Zentriq Remote Support agent (the ZentriqRemoteSupport service), the same way as for the
/// Zentriq Agent itself: while tamper protection is on (and Software Deployment › Settings › "Protect the remote support
/// agent" is on), administrators on the device cannot stop, disable, reconfigure or delete the service, change its
/// registry key or its program files, and its uninstaller fails. The periodic check (every two minutes) restores the
/// startup type, starts the service when it was stopped and undoes permission changes; when the agent was removed anyway,
/// the check-in reports it and the server installs it again. Installing and removing it from the console keeps working
/// (the agent lifts the protection first), as does a pause of tamper protection with a code.
/// </summary>
internal static class RemoteGuard
{
    private const string Service = RemoteSupportActions.ServiceName;
    private const string Key = @"SYSTEM\CurrentControlSet\Services\" + Service;
    /// <summary>Administrators: query, start and interrogate only — no stop, configuration change or delete.</summary>
    public const string HardenedSddl = "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWRPLOCRRC;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
    public const string DefaultSddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
    private static readonly object Gate = new object();
    private static bool? _hardened;
    private static string? _folder;

    private static bool Wanted => TamperGuard.Wanted && AppControlAgent.Policy.ProtectRemoteAgent;

    /// <summary>on, off, absent (not installed) or missing (removed behind the console's back).</summary>
    public static string State
    {
        get
        {
            if (!RemoteSupportActions.Installed()) return Marker() ? "missing" : "absent";
            return _hardened == true ? "on" : "off";
        }
    }

    /// <summary>Applies the wanted state (start, check-in, policy change and the periodic check).</summary>
    public static void Sync()
    {
        if (!UninstallProtection.CanManage) return;
        lock (Gate)
        {
            var installed = RemoteSupportActions.Installed();
            if (!installed)
            {
                if (Marker() && Wanted && _reported != true)
                {
                    _reported = true;
                    TamperGuard.Record("tamper", "The protected remote support agent was removed from this device. Zentriq installs it again.");
                }
                _hardened = null;
                return;
            }
            _reported = null;
            if (!Wanted) { if (_hardened != false) Set(false); SetMarker(false); return; }
            if (_hardened != true) Set(true);
            Check();
        }
    }

    private static bool? _reported;

    /// <summary>Before the console removes or reinstalls it: protection off, nothing reported as removed.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            try { Set(false); } catch (Exception ex) { Log.Warn("Remote support protection: " + ex.Message); }
            SetMarker(false);
        }
    }

    private static void Set(bool hardened)
    {
        var ok = Sc($"sdset {Service} {(hardened ? HardenedSddl : DefaultSddl)}");
        try { LockKey(hardened); } catch (Exception ex) { ok = false; Log.Warn("Remote support protection (registry): " + ex.Message); }
        try { LockFolder(hardened); } catch (Exception ex) { ok = false; Log.Warn("Remote support protection (folder): " + ex.Message); }
        if (hardened) SetMarker(true);
        if (ok) _hardened = hardened;
        Log.Info(hardened ? "Remote support agent protected: stopping, disabling or removing it on the device needs a code from the Zentriq console."
                          : "Remote support agent protection is off on this device.");
    }

    private static void Check()
    {
        var changed = new List<string>();
        if (ScOut($"sdshow {Service}") is { } sd && !TamperGuard.DaclOf(sd).Equals(TamperGuard.DaclOf(HardenedSddl), StringComparison.OrdinalIgnoreCase))
        {
            changed.Add("its service permissions were changed");
            Sc($"sdset {Service} {HardenedSddl}");
        }
        var start = StartType();
        if (start is 3 or 4)
        {
            changed.Add($"its startup type was set to {(start == 4 ? "Disabled" : "Manual")}");
            Sc($"config {Service} start= auto");
        }
        try
        {
            using var sc = new ServiceController(Service);
            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                changed.Add("it was stopped");
                sc.Start();
            }
        }
        catch (Exception ex) { Log.Warn("Remote support protection: could not start the service: " + ex.Message); }
        if (changed.Count > 0) TamperGuard.Record("tamper", "Someone changed the protected remote support agent: " + string.Join("; ", changed) + ". Restored.");
    }

    private static void LockKey(bool hardened)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey(Key, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadKey | RegistryRights.ChangePermissions);
        if (k is null) return;
        var sec = new RegistrySecurity();
        sec.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit;
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), hardened ? RegistryRights.ReadKey : RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), RegistryRights.ReadKey, inherit, PropagationFlags.None, AccessControlType.Allow));
        k.SetAccessControl(sec);
    }

    /// <summary>The folder of the service's program (from ImagePath), when it is under Program Files.</summary>
    private static string? Folder()
    {
        if (_folder is not null) return _folder;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(Key, false);
            var image = (k?.GetValue("ImagePath") as string ?? "").Trim();
            if (image.StartsWith("\"")) image = image.Substring(1, Math.Max(0, image.IndexOf('"', 1) - 1));
            else if (image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) is var i and > 0) image = image.Substring(0, i + 4);
            var dir = Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(image));
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (dir is not null && dir.StartsWith(pf + "\\", StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir)) _folder = dir;
        }
        catch (Exception) { }
        return _folder;
    }

    private static void LockFolder(bool hardened)
    {
        if (Folder() is not { } dir) return;
        var sec = new DirectorySecurity();
        if (hardened)
        {
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        else sec.SetAccessRuleProtection(false, false);
        Directory.SetAccessControl(dir, sec);
    }

    private static int StartType()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(Key, false);
            return k?.GetValue("Start") is int i ? i : 0;
        }
        catch (Exception) { return 0; }
    }

    private static bool Marker()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(UninstallProtection.KeyPath, false);
            return k?.GetValue("RemoteSupportProtected") is string s && s == "1";
        }
        catch (Exception) { return false; }
    }

    private static void SetMarker(bool on)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(UninstallProtection.KeyPath, true);
            if (k is null) return;
            if (on) k.SetValue("RemoteSupportProtected", "1", RegistryValueKind.String); else k.DeleteValue("RemoteSupportProtected", false);
        }
        catch (Exception ex) { Log.Warn("Remote support protection marker: " + ex.Message); }
    }

    private static bool Sc(string args) => ScOut(args) is not null;

    private static string? ScOut(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception) { return null; }
    }
}
