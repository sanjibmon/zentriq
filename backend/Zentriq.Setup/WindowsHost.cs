using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;

namespace Zentriq.Setup;

/// <summary>Windows-specific operations used by provisioning (ACLs, services, processes, tasks).</summary>
public static class WindowsHost
{
    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator) || id.IsSystem;
    }

    public static SecurityIdentifier System => new(WellKnownSidType.LocalSystemSid, null);
    public static SecurityIdentifier Administrators => new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>SID of a service's virtual account (NT SERVICE\name). The service must exist.</summary>
    public static SecurityIdentifier ServiceSid(string serviceName) =>
        (SecurityIdentifier)new NTAccount("NT SERVICE", serviceName).Translate(typeof(SecurityIdentifier));

    /// <summary>
    /// Replaces the directory ACL with: SYSTEM + Administrators full control, plus the given grants.
    /// Inheritance from the parent is removed so ProgramData's default "Users: read" does not apply.
    /// </summary>
    /// <summary>Adds an inherited read rule for <paramref name="sid"/> to a folder (other rules stay as they are).</summary>
    public static void GrantRead(string path, SecurityIdentifier sid)
    {
        var di = new DirectoryInfo(path);
        var sec = di.GetAccessControl();
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var exists = sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(r => r.IdentityReference == sid && r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute);
        if (exists) return;
        sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);
    }

    public static void SecureDirectory(string path, params (SecurityIdentifier Sid, FileSystemRights Rights)[] grants)
    {
        Directory.CreateDirectory(path);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.SetOwner(Administrators);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var (sid, rights) in grants)
            sec.AddAccessRule(new FileSystemAccessRule(sid, rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(sec);
    }

    /// <summary>Explicit, non-inherited file ACL (used for secrets files).</summary>
    public static void SecureFile(string path, params (SecurityIdentifier Sid, FileSystemRights Rights)[] grants)
    {
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        sec.SetOwner(Administrators);
        sec.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        foreach (var (sid, rights) in grants)
            sec.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(sec);
    }

    public sealed record ProcessResult(int ExitCode, string Output);

    /// <summary>Runs a process; arguments are passed as a list (no shell). stdin is optional.</summary>
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout, string? stdin = null,
        IDictionary<string, string>? env = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = stdin is not null, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new SetupException($"Could not start {fileName}.");
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        if (stdin is not null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new SetupException($"{Path.GetFileName(fileName)} did not finish within {timeout.TotalMinutes:N0} minutes.");
        }
        p.WaitForExit();
        return new ProcessResult(p.ExitCode, sb.ToString());
    }

    public static string SystemExe(string name) => Path.Combine(Environment.SystemDirectory, name);

    // ---- services ---------------------------------------------------------------------------

    public static bool ServiceExists(string name) => ServiceController.GetServices().Any(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static async Task ConfigureRecoveryAsync(string service, string? dependsOn, Reporter log)
    {
        // Restart after 5 s, 15 s, then 60 s; reset the failure counter after a day. Also on non-crash failures.
        await Sc(log, "failure", service, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
        await Sc(log, "failureflag", service, "1");
        await Sc(log, "config", service, "start=", "delayed-auto");
        if (!string.IsNullOrEmpty(dependsOn)) await Sc(log, "config", service, "depend=", dependsOn);
        await Sc(log, "sidtype", service, "unrestricted");
    }

    /// <summary>Sets a service to Disabled (it stays registered; ConfigureRecoveryAsync turns it back on).</summary>
    public static Task DisableAsync(string service, Reporter log) => Sc(log, "config", service, "start=", "disabled");

    private static async Task Sc(Reporter log, params string[] args)
    {
        var r = await RunAsync(SystemExe("sc.exe"), args, TimeSpan.FromSeconds(30));
        if (r.ExitCode != 0) log.Warn($"sc.exe {string.Join(' ', args)} returned {r.ExitCode}: {r.Output.Trim()}");
    }

    public static void StartAndWait(string name, TimeSpan timeout)
    {
        using var sc = new ServiceController(name);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running) return;
        if (sc.Status is ServiceControllerStatus.StopPending or ServiceControllerStatus.StartPending)
            sc.WaitForStatus(sc.Status == ServiceControllerStatus.StopPending ? ServiceControllerStatus.Stopped : ServiceControllerStatus.Running, timeout);
        sc.Refresh();
        if (sc.Status != ServiceControllerStatus.Running) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public static void StopAndWait(string name, TimeSpan timeout)
    {
        if (!ServiceExists(name)) return;
        using var sc = new ServiceController(name);
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.CanStop && sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    public static void Restart(string name, TimeSpan timeout)
    {
        StopAndWait(name, timeout);
        StartAndWait(name, timeout);
    }

    public static string ServiceStatus(string name)
    {
        if (!ServiceExists(name)) return "not installed";
        using var sc = new ServiceController(name);
        return sc.Status.ToString();
    }

    // ---- scheduled tasks --------------------------------------------------------------------

    public static async Task RegisterTaskAsync(string name, string command, string arguments, string schedule, string? modifierOrTime, Reporter log)
    {
        var args = new List<string> { "/Create", "/TN", name, "/TR", $"\"{command}\" {arguments}", "/RU", "SYSTEM", "/RL", "HIGHEST", "/F", "/SC", schedule };
        if (schedule == "MINUTE" && modifierOrTime is not null) { args.Add("/MO"); args.Add(modifierOrTime); }
        if (schedule == "DAILY" && modifierOrTime is not null) { args.Add("/ST"); args.Add(modifierOrTime); }
        var r = await RunAsync(SystemExe("schtasks.exe"), args, TimeSpan.FromSeconds(30));
        if (r.ExitCode != 0) throw new SetupException($"Could not register scheduled task {name}: {r.Output.Trim()}");
    }

    public static async Task RemoveTaskAsync(string name)
    {
        await RunAsync(SystemExe("schtasks.exe"), ["/Delete", "/TN", name, "/F"], TimeSpan.FromSeconds(30));
    }
}
