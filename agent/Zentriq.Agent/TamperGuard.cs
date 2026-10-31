using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using Zentriq.Inventory;

namespace Zentriq.Agent;

/// <summary>
/// Tamper protection (on together with uninstall protection, Agents › Settings). While it is on, nobody on the device —
/// local or domain administrators included — can stop, disable, reconfigure or delete the agent service, change its
/// registry key or its program files:
/// <list type="bullet">
/// <item>the service grants Administrators query, start and stop requests only (no change of configuration or startup type,
/// no delete or permission changes); a stop or restart request (Services console, net stop, sc stop) is held until a code
/// is entered in the dialog the agent shows (see AgentService / StopPrompt), otherwise the service keeps running;</item>
/// <item>HKLM\SYSTEM\CurrentControlSet\Services\ZentriqAgent and the program folder are read-only for Administrators;</item>
/// <item>the service checks every two minutes that this is still so (an administrator can take ownership of an object),
/// restores what was changed and reports it to the console;</item>
/// <item>a SYSTEM scheduled task (at startup and every 15 minutes) starts the service again and restores its startup type,
/// in case it was disabled behind the service's back and the device restarted.</item>
/// </list>
/// Stopping or disabling on the device needs the same one-hour code as uninstalling (the device's own code or the code for
/// all agents): <c>ZentriqAgent.exe --stop|--disable|--pause &lt;code&gt;</c>. The command hands the code to the service over
/// a local pipe; the service (SYSTEM) checks it. The console can pause protection, restart or disable the agent at any time.
/// Windows Installer runs as SYSTEM, so updates and uninstalling with a code keep working.
/// </summary>
internal static class TamperGuard
{
    public const string DefaultSddl = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
    public const string HardenedSddl = "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWRPWPLOCRRC;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
    public const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + Program.ServiceName;
    public const string TaskName = @"\Zentriq\Zentriq Agent watchdog";
    public static readonly TimeSpan PauseLength = TimeSpan.FromHours(1);

    private static readonly object Gate = new object();
    private static bool? _hardened;
    private static string? _serviceKeySddl, _folderSddl;
    private static DateTime _taskChecked;

    public static string InstallFolder => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    private static SecurityIdentifier Sid(WellKnownSidType t) => new SecurityIdentifier(t, null);

    // ---- state (HKLM\SOFTWARE\Zentriq\Agent\Protection, SYSTEM only) ---------------------------------------------

    public static DateTime? PausedUntil => Marker("PausedUntil") is { } d && d > DateTime.UtcNow ? d : (DateTime?)null;
    public static DateTime? StoppedByCode => Marker("StoppedByCode");
    public static DateTime? DisabledByCode => Marker("DisabledByCode");

    /// <summary>Protection is wanted: uninstall protection is on and no authorized pause, stop or disable is in effect.</summary>
    public static bool Wanted => UninstallProtection.Enabled && PausedUntil is null && StoppedByCode is null && DisabledByCode is null;

    private static DateTime? Marker(string name)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(UninstallProtection.KeyPath, false);
            return k?.GetValue(name) is string s && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException) { return null; }
    }

    private static void SetMarker(string name, DateTime? value)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey(UninstallProtection.KeyPath, true);
        if (k is null) return;
        if (value is null) k.DeleteValue(name, false);
        else k.SetValue(name, value.Value.ToUniversalTime().ToString("o"), RegistryValueKind.String);
    }

    // ---- events for the console --------------------------------------------------------------------------------

    /// <summary>
    /// Records something to report at the next check-in: "tamper" (a change that was undone, a wrong code) or "control"
    /// (stopped, disabled or paused with a code). Kept with the protection key (SYSTEM only), at most 20.
    /// </summary>
    public static void Record(string kind, string text)
    {
        if (kind == "tamper") Log.Warn("Tamper protection: " + text); else Log.Info("Tamper protection: " + text);
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(UninstallProtection.KeyPath, true);
            if (k is null) return;
            var list = (k.GetValue("Events") as string[] ?? new string[0]).ToList();
            list.Add(kind + "|" + DateTime.UtcNow.ToString("o") + "|" + text.Replace('\n', ' '));
            k.SetValue("Events", list.Skip(Math.Max(0, list.Count - 20)).ToArray(), RegistryValueKind.MultiString);
        }
        catch (Exception ex) { Log.Warn("Could not keep the protection event: " + ex.Message); }
        if (kind == "tamper") AgentService.RequestServerWake();   // report it now
    }

    public static List<Dictionary<string, object?>> PendingEvents()
    {
        var result = new List<Dictionary<string, object?>>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(UninstallProtection.KeyPath, false);
            foreach (var e in k?.GetValue("Events") as string[] ?? new string[0])
            {
                var p = e.Split(new[] { '|' }, 3);
                if (p.Length == 3) result.Add(new Dictionary<string, object?> { ["kind"] = p[0], ["at"] = p[1], ["text"] = p[2] });
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException) { }
        return result;
    }

    public static void ClearEvents(int count)
    {
        if (count == 0) return;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(UninstallProtection.KeyPath, true);
            if (k?.GetValue("Events") is not string[] all) return;
            var rest = all.Skip(count).ToArray();
            if (rest.Length == 0) k.DeleteValue("Events", false); else k.SetValue("Events", rest, RegistryValueKind.MultiString);
        }
        catch (Exception ex) { Log.Warn("Could not clear reported protection events: " + ex.Message); }
    }

    // ---- applying ----------------------------------------------------------------------------------------------

    /// <summary>Brings the device to the wanted state (called at start, at every check-in and by the periodic check).</summary>
    public static void Sync()
    {
        if (!UninstallProtection.CanManage) return;
        lock (Gate)
        {
            // An expired pause ends here.
            if (Marker("PausedUntil") is { } until && until <= DateTime.UtcNow)
            {
                try { SetMarker("PausedUntil", null); } catch (Exception) { }
                Record("control", "Pause of tamper protection ended; protection is on again.");
            }
            Set(Wanted);
        }
    }

    /// <summary>Hardens or relaxes the service, its registry key, the program folder and the watchdog task.</summary>
    public static void Set(bool hardened)
    {
        if (_hardened == hardened) return;
        var ok = Sc($"sdset {Program.ServiceName} {(hardened ? HardenedSddl : DefaultSddl)}", out _);
        try { LockServiceKey(hardened); }
        catch (Exception ex) { ok = false; Log.Warn("Could not change the agent service registry key permissions: " + ex.Message); }
        try { LockFolder(hardened); }
        catch (Exception ex) { ok = false; Log.Warn("Could not change the agent folder permissions: " + ex.Message); }
        try { if (hardened) EnsureWatchdog(); else RemoveWatchdog(); }
        catch (Exception ex) { Log.Warn("Watchdog task: " + ex.Message); }
        if (hardened && StartType() != 2) Sc($"config {Program.ServiceName} start= delayed-auto", out _);
        if (ok) _hardened = hardened;
        Log.Info(hardened ? "Tamper protection is on: stopping, disabling or changing the agent needs a code from the Zentriq console."
                          : "Tamper protection is off on this device: administrators can stop or disable the agent service.");
    }

    private static void LockServiceKey(bool hardened)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey(ServiceKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadKey | RegistryRights.ChangePermissions);
        if (k is null) return;
        var sec = new RegistrySecurity();
        sec.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit;
        sec.AddAccessRule(new RegistryAccessRule(Sid(WellKnownSidType.LocalSystemSid), RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(Sid(WellKnownSidType.BuiltinAdministratorsSid), hardened ? RegistryRights.ReadKey : RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new RegistryAccessRule(Sid(WellKnownSidType.BuiltinUsersSid), RegistryRights.ReadKey, inherit, PropagationFlags.None, AccessControlType.Allow));
        k.SetAccessControl(sec);
        _serviceKeySddl = hardened ? ServiceKeySddl() : null;
    }

    private static string? ServiceKeySddl()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hklm.OpenSubKey(ServiceKey, false);
        return k?.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }

    private static void LockFolder(bool hardened)
    {
        var dir = InstallFolder;
        // Only the installed program folder (not a copy run from elsewhere, e.g. during troubleshooting).
        if (!dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)) return;
        var sec = new DirectorySecurity();
        if (hardened)
        {
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.LocalSystemSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.BuiltinAdministratorsSid), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(Sid(WellKnownSidType.BuiltinUsersSid), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        }
        else sec.SetAccessRuleProtection(false, false);   // back to the permissions inherited from Program Files
        Directory.SetAccessControl(dir, sec);
        _folderSddl = hardened ? FolderSddl() : null;
    }

    private static string? FolderSddl()
    {
        try { return Directory.GetAccessControl(InstallFolder, AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access); }
        catch (Exception) { return null; }
    }

    // ---- watchdog task -----------------------------------------------------------------------------------------

    private static void EnsureWatchdog()
    {
        if (Run("schtasks.exe", $"/query /tn \"{TaskName}\"", out _) == 0) return;
        var exe = Path.Combine(InstallFolder, "ZentriqAgent.exe");
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <RegistrationInfo><Author>Zentriq</Author><Description>Keeps the Zentriq Agent service enabled and running while tamper protection is on.</Description></RegistrationInfo>\n" +
            "  <Triggers>\n" +
            "    <BootTrigger><Delay>PT2M</Delay><Enabled>true</Enabled></BootTrigger>\n" +
            "    <TimeTrigger><StartBoundary>2026-01-01T00:00:00</StartBoundary><Repetition><Interval>PT15M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><Enabled>true</Enabled></TimeTrigger>\n" +
            "  </Triggers>\n" +
            "  <Principals><Principal id=\"System\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
            "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT5M</ExecutionTimeLimit>" +
            "<AllowHardTerminate>true</AllowHardTerminate><Enabled>true</Enabled></Settings>\n" +
            $"  <Actions Context=\"System\"><Exec><Command>{System.Security.SecurityElement.Escape(exe)}</Command><Arguments>--watchdog</Arguments></Exec></Actions>\n" +
            "</Task>\n";
        Directory.CreateDirectory(AgentPaths.Root);
        var file = Path.Combine(AgentPaths.Root, "watchdog-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            if (Run("schtasks.exe", $"/create /tn \"{TaskName}\" /xml \"{file}\" /f", out var output) != 0) throw new InvalidOperationException("schtasks: " + output.Trim());
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    public static void RemoveWatchdog()
    {
        if (Run("schtasks.exe", $"/query /tn \"{TaskName}\"", out _) == 0) Run("schtasks.exe", $"/delete /tn \"{TaskName}\" /f", out _);
    }

    // ---- periodic check --------------------------------------------------------------------------------------

    /// <summary>Every two minutes in the service: re-applies protection and undoes changes made behind its back.</summary>
    public static void Loop(CancellationToken ct)
    {
        if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20))) return;
        while (!ct.IsCancellationRequested)
        {
            try { Check(); } catch (Exception ex) { Log.Warn("Tamper protection check failed: " + ex.Message); }
            if (ct.WaitHandle.WaitOne(TimeSpan.FromMinutes(2))) return;
        }
    }

    public static void Check()
    {
        if (!UninstallProtection.CanManage) return;
        Sync();
        if (!Wanted || _hardened != true) return;
        lock (Gate)
        {
            var changed = new List<string>();
            if (Sc($"sdshow {Program.ServiceName}", out var sd) && !DaclOf(sd).Equals(DaclOf(HardenedSddl), StringComparison.OrdinalIgnoreCase))
            {
                changed.Add("the service permissions were changed");
                Sc($"sdset {Program.ServiceName} {HardenedSddl}", out _);
            }
            var start = StartType();
            if (start != 2)
            {
                changed.Add($"the startup type was set to {(start == 4 ? "Disabled" : start == 3 ? "Manual" : start.ToString())}");
                Sc($"config {Program.ServiceName} start= delayed-auto", out _);
            }
            if (_serviceKeySddl is not null && ServiceKeySddl() is { } now && now != _serviceKeySddl)
            {
                changed.Add("the permissions of the service registry key were changed");
                try { LockServiceKey(true); } catch (Exception ex) { Log.Warn("Could not restore the service registry key: " + ex.Message); }
            }
            if (_folderSddl is not null && FolderSddl() is { } f && f != _folderSddl)
            {
                changed.Add("the permissions of the program folder were changed");
                try { LockFolder(true); } catch (Exception ex) { Log.Warn("Could not restore the program folder permissions: " + ex.Message); }
            }
            if (DateTime.UtcNow - _taskChecked > TimeSpan.FromMinutes(10))
            {
                _taskChecked = DateTime.UtcNow;
                if (Run("schtasks.exe", $"/query /tn \"{TaskName}\"", out _) != 0)
                {
                    changed.Add("the watchdog task was deleted");
                    try { EnsureWatchdog(); } catch (Exception ex) { Log.Warn("Watchdog task: " + ex.Message); }
                }
            }
            if (changed.Count > 0) Record("tamper", "Someone changed the protected agent: " + string.Join("; ", changed) + ". Restored.");
        }
    }

    /// <summary>The "D:" part of a security descriptor (sdshow also prints the audit part).</summary>
    public static string DaclOf(string sddl)
    {
        var s = sddl.Trim();
        var d = s.IndexOf("D:", StringComparison.Ordinal);
        if (d < 0) return s;
        var end = s.IndexOf("S:", d, StringComparison.Ordinal);
        return (end < 0 ? s.Substring(d) : s.Substring(d, end - d)).Trim();
    }

    /// <summary>2 automatic, 3 manual, 4 disabled (HKLM\SYSTEM\...\Services\ZentriqAgent\Start); 0 when unknown.</summary>
    public static int StartType()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(ServiceKey, false);
            return k?.GetValue("Start") is int i ? i : 0;
        }
        catch (Exception) { return 0; }
    }

    // ---- watchdog (scheduled task, SYSTEM) -------------------------------------------------------------------

    /// <summary>ZentriqAgent.exe --watchdog: restores the startup type and starts the service unless a code stopped or disabled it.</summary>
    public static int Watchdog()
    {
        if (!UninstallProtection.CanManage) { Console.Error.WriteLine("--watchdog runs as SYSTEM (scheduled task)."); return 1; }
        if (!UninstallProtection.Enabled) { RemoveWatchdog(); return 0; }
        if (DisabledByCode is not null || PausedUntil is not null) return 0;
        if (StoppedByCode is { } stopped)
        {
            var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount & int.MaxValue);
            if (stopped > boot) return 0;   // stopped with a code since the last restart: stays stopped until the next one
            SetMarker("StoppedByCode", null);
        }
        var start = StartType();
        if (start != 2)
        {
            Record("tamper", $"The agent service startup type was {(start == 4 ? "Disabled" : "Manual")}; set back to Automatic by the watchdog.");
            Sc($"config {Program.ServiceName} start= delayed-auto", out _);
        }
        using var sc = new ServiceController(Program.ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped)
        {
            Record("tamper", "The agent service was found stopped; started again by the watchdog.");
            sc.Start();
        }
        return 0;
    }

    // ---- authorized stop, disable, pause -------------------------------------------------------------------

    /// <summary>Pause (1 hour, or until the console resumes), stop (until started again or the next restart) or disable.</summary>
    public static string Perform(string action, string who, TimeSpan? pause = null)
    {
        switch (action)
        {
            case "pause":
            {
                var until = DateTime.UtcNow + (pause ?? PauseLength);
                SetMarker("PausedUntil", until);
                Set(false);
                Record("control", $"Tamper protection paused until {until:yyyy-MM-dd HH:mm} UTC ({who}).");
                return $"Tamper protection is paused until {until.ToLocalTime():HH:mm}. Administrators can stop or disable the agent service until then.";
            }
            case "resume":
                SetMarker("PausedUntil", null);
                Sync();
                Record("control", $"Tamper protection resumed ({who}).");
                return "Tamper protection is on again.";
            case "stop":
                SetMarker("StoppedByCode", DateTime.UtcNow);
                Set(false);
                Record("control", $"Agent service stopped ({who}); it starts again when the device restarts or when started by hand.");
                StopSoon(disable: false);
                return "The Zentriq Agent service stops now. It starts again at the next restart of the device, or with 'ZentriqAgent.exe --enable'.";
            case "disable":
                SetMarker("DisabledByCode", DateTime.UtcNow);
                Set(false);
                Record("control", $"Agent service disabled ({who}); the device stays offline in the console until it is enabled again.");
                StopSoon(disable: true);
                return "The Zentriq Agent service is disabled and stops now. Enable it again with 'ZentriqAgent.exe --enable' (as administrator).";
            default:
                throw new ArgumentException("Unknown action " + action);
        }
    }

    /// <summary>Stops (and optionally disables) this service shortly after the answer has gone out.</summary>
    private static void StopSoon(bool disable)
    {
        StopGate.AuthorizeInternal(disable ? "disabled with a code" : "stopped with a code");
        new Thread(() =>
        {
            Thread.Sleep(1500);
            if (disable) Sc($"config {Program.ServiceName} start= disabled", out _);
            try { using var sc = new ServiceController(Program.ServiceName); sc.Stop(); }
            catch (Exception ex) { Log.Warn("Could not stop the agent service: " + ex.Message); }
        }) { IsBackground = true, Name = "stop" }.Start();
    }

    /// <summary>The service started again (by hand or at boot): authorized stop / disable markers end, protection returns.</summary>
    public static void OnServiceStart()
    {
        if (!UninstallProtection.CanManage || !UninstallProtection.Enabled) return;
        try
        {
            var was = DisabledByCode is not null ? "disabled" : StoppedByCode is not null ? "stopped" : null;
            SetMarker("StoppedByCode", null);
            SetMarker("DisabledByCode", null);
            if (was is not null) Record("control", $"Agent service started again after it was {was} with a code.");
        }
        catch (Exception ex) { Log.Warn("Tamper protection: " + ex.Message); }
    }

    /// <summary>For the console: "on", "paused" (with the time) or "off"; null when not running as the service.</summary>
    public static object? ReportedState()
    {
        if (!UninstallProtection.CanManage) return null;
        if (!UninstallProtection.Enabled) return new Dictionary<string, object?> { ["state"] = "off" };
        if (PausedUntil is { } until) return new Dictionary<string, object?> { ["state"] = "paused", ["until"] = until.ToString("o") };
        return new Dictionary<string, object?> { ["state"] = _hardened == true ? "on" : "pending" };
    }

    // ---- local control pipe ------------------------------------------------------------------------------------

    public const string PipeName = "ZentriqAgentControl";
    private static readonly List<DateTime> Failures = new List<DateTime>();

    /// <summary>The service side: answers status requests and checks codes typed with --stop / --disable / --pause.</summary>
    public static void PipeLoop(CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(Sid(WellKnownSidType.LocalSystemSid), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(Sid(WellKnownSidType.BuiltinAdministratorsSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
                var wait = pipe.WaitForConnectionAsync(ct);
                try { wait.Wait(ct); } catch (OperationCanceledException) { return; } catch (AggregateException) when (ct.IsCancellationRequested) { return; }
                var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var read = reader.ReadLineAsync();
                if (!read.Wait(TimeSpan.FromSeconds(10)) || read.Result is null) continue;
                string answer;
                try { answer = Handle(read.Result); }
                catch (Exception ex) { answer = Reply(false, ex.Message); }
                writer.WriteLine(answer);
                try { pipe.WaitForPipeDrain(); } catch (IOException) { }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warn("Local control pipe: " + ex.Message);
                if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) return;
            }
        }
    }

    private static string Reply(bool ok, string message, object? extra = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["ok"] = ok, ["message"] = message, ["status"] = extra });

    private static object Status() => new Dictionary<string, object?>
    {
        ["protected"] = UninstallProtection.Enabled, ["active"] = Wanted, ["pausedUntil"] = PausedUntil?.ToString("o"),
        ["stopRequest"] = StopGate.Pending,
    };

    private static string Handle(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var action = doc.RootElement.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
        if (action == "status") return Reply(true, Wanted ? "on" : "off", Status());
        // Contacting the server and restarting need no code (they never weaken the protection).
        if (action == "checkin" || action == "collect")
        {
            var r = AgentService.RunNow(action == "collect", TimeSpan.FromSeconds(150));
            return r is null ? Reply(false, "The agent did not finish contacting the server within 2½ minutes. Run 'ZentriqAgent.exe --diagnose'.")
                : Reply(r.Ok, r.Message + (r.Ok ? "" : " Run 'ZentriqAgent.exe --diagnose' for details."));
        }
        // The code dialog for a stop or restart from the Services console (net stop, sc stop) that is waiting.
        if (action == "cancel-stop") { StopGate.Cancel(); return Reply(true, "Cancelled; the agent keeps running."); }
        if (action == "authorize-stop")
        {
            if (!StopGate.Pending) return Reply(false, "No stop request is waiting; the dialog can be closed.");
            lock (Failures)
            {
                Failures.RemoveAll(f => f < DateTime.UtcNow.AddMinutes(-15));
                if (Failures.Count >= 5) return Reply(false, "Too many wrong codes. Wait 15 minutes and try again with a new code from the Zentriq console.");
            }
            if (!CodeAccepted(code, out var refused))
            {
                lock (Failures) Failures.Add(DateTime.UtcNow);
                Record("tamper", "Wrong code entered to stop the agent service: " + refused);
                return Reply(false, refused);
            }
            var restart = doc.RootElement.TryGetProperty("restart", out var rs) && rs.ValueKind == JsonValueKind.True;
            if (restart) Record("control", "Agent service restarted with a code entered in the dialog (Services console, net stop or sc stop).");
            else
            {
                SetMarker("StoppedByCode", DateTime.UtcNow);
                Set(false);
                Record("control", "Agent service stopped with a code entered in the dialog (Services console, net stop or sc stop); it starts again when the device restarts, or with --enable.");
            }
            StopGate.Authorize(restart);
            return Reply(true, restart ? "Code accepted. The Zentriq Agent service restarts now." : "Code accepted. The Zentriq Agent service stops now.");
        }
        if (action == "restart")
        {
            Record("control", "Agent service restart requested on the device.");
            StopGate.AuthorizeInternal("restart requested on the device (--restart)");
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Process.Start(new ProcessStartInfo(cmd, $"/c ping -n 3 127.0.0.1 >nul & net stop {Program.ServiceName} & net start {Program.ServiceName}")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
            return Reply(true, "The Zentriq Agent service restarts now and contacts the server within a minute.");
        }
        if (action != "stop" && action != "disable" && action != "pause") return Reply(false, "Unknown request.");
        if (UninstallProtection.Enabled)
        {
            lock (Failures)
            {
                Failures.RemoveAll(f => f < DateTime.UtcNow.AddMinutes(-15));
                if (Failures.Count >= 5) return Reply(false, "Too many wrong codes. Wait 15 minutes and try again with a new code from the Zentriq console.");
            }
            if (!CodeAccepted(code, out var why))
            {
                lock (Failures) Failures.Add(DateTime.UtcNow);
                Record("tamper", $"Refused to {action} the agent service: {why}");
                return Reply(false, why);
            }
        }
        return Reply(true, Perform(action, "code entered on the device"), Status());
    }

    /// <summary>The device's own one-hour code (from its key) or the current code for all agents.</summary>
    public static bool CodeAccepted(string? code, out string error)
    {
        var key = UninstallProtection.Key();
        var deviceError = "";
        if (key is not null && AgentCodes.VerifyUninstallCode(key, code, DateTime.UtcNow, out deviceError)) { error = ""; return true; }
        string? hash = null; DateTime? expires = null;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = hklm.OpenSubKey(UninstallProtection.KeyPath, false);
            hash = k?.GetValue("AllCodeHash") as string;
            if (k?.GetValue("AllCodeExpires") is string e && DateTime.TryParse(e, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var x)) expires = x;
        }
        catch (Exception) { }
        if (!string.IsNullOrEmpty(hash) && AgentCodes.VerifyUninstallCodeForAll(hash, expires, code, DateTime.UtcNow, out var allError)) { error = ""; return true; }
        error = AgentCodes.Normalize(code, AgentCodes.UninstallLength) is null
            ? "The code must have 16 letters and digits (for example ABCD-EFGH-JKMN-PQRS). Get it from the Zentriq console (Agents › Uninstall code, or the device's agent menu)."
            : (deviceError.Contains("expired") ? deviceError : "The code is not valid for this device. Get a current code from the Zentriq console (valid for one hour).");
        return false;
    }

    /// <summary>The command side (any administrator): sends a request to the running service.</summary>
    public static int Client(string action, string? code)
    {
        try
        {
            using var sc = new ServiceController(Program.ServiceName);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                Console.Error.WriteLine($"The Zentriq Agent service is {sc.Status.ToString().ToLowerInvariant()}." +
                    (action == "status" ? "" : " Start it first ('ZentriqAgent.exe --enable' or Start-Service ZentriqAgent)."));
                return action == "status" ? 0 : 1;
            }
        }
        catch (InvalidOperationException) { Console.Error.WriteLine("The Zentriq Agent service is not installed."); return 1; }
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(5000);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?> { ["action"] = action, ["code"] = code?.Trim().Trim('"') }));
            var line = reader.ReadLine() ?? "{}";
            using var doc = JsonDocument.Parse(line);
            var ok = doc.RootElement.TryGetProperty("ok", out var o) && o.GetBoolean();
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "";
            if (action == "status")
            {
                var st = doc.RootElement.GetProperty("status");
                var paused = st.TryGetProperty("pausedUntil", out var p) && p.ValueKind == JsonValueKind.String ? DateTime.Parse(p.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind) : (DateTime?)null;
                Console.WriteLine(st.GetProperty("active").GetBoolean() ? "on (stopping, disabling and uninstalling need a code from the Zentriq console)"
                    : paused is { } pu ? $"paused until {pu.ToLocalTime():HH:mm}" : "off");
                return 0;
            }
            (ok ? Console.Out : Console.Error).WriteLine(message);
            return ok ? 0 : 1;
        }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("Run this command as administrator."); return 1; }
        catch (System.TimeoutException) { Console.Error.WriteLine("The Zentriq Agent service did not answer. Try again in a minute."); return 1; }
    }

    /// <summary>--enable: startup type Automatic and start (after a stop or disable with a code).</summary>
    public static int Enable()
    {
        Sc($"config {Program.ServiceName} start= delayed-auto", out var output);
        try
        {
            using var sc = new ServiceController(Program.ServiceName);
            if (sc.Status == ServiceControllerStatus.Stopped) { sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
            Console.WriteLine("The Zentriq Agent service is running (startup type Automatic).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not start the Zentriq Agent service: " + ex.Message + (string.IsNullOrWhiteSpace(output) ? "" : " " + output.Trim()));
            return 1;
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static bool Sc(string arguments, out string output)
    {
        var exit = Run("sc.exe", arguments, out output);
        if (exit != 0) Log.Warn($"sc {arguments.Split(' ')[0]} exit {exit}: {output.Trim()}");
        return exit == 0;
    }

    private static int Run(string exe, string arguments, out string output)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch (Exception) { } output = "timed out"; return -1; }
            output = o.Result + e.Result;
            return p.ExitCode;
        }
        catch (Exception ex) { output = ex.Message; return -1; }
    }
}
