using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;

namespace Zentriq.Agent;

/// <summary>
/// Agent update requested in the console. The package is downloaded from this agent's own Zentriq server over the
/// mutually authenticated agent connection, checked against the SHA-256 the server sent with the request, and
/// installed with Windows Installer (major upgrade): the service stops, files are replaced, the service starts again
/// and keeps its enrollment and certificate. The new version is confirmed by the next check-in.
/// </summary>
internal static class AgentUpdater
{
    public static string Folder => Path.Combine(AgentPaths.Root, "Updates");

    /// <returns>(ok, state, message). State "started" means msiexec is running; the server waits for the new version.</returns>
    public static (bool Ok, string? State, string Message) Start(ServerClient client, string actionId, string targetVersion, string? sha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sha256) || sha256!.Length != 64)
            return (false, null, "The server did not send a package checksum; update refused.");
        if (string.Equals(targetVersion, Program.Version, StringComparison.OrdinalIgnoreCase))
            return (true, "current", $"The agent is already version {Program.Version}.");
        if (Version.TryParse(targetVersion, out var target) && Version.TryParse(Program.Version, out var mine) && target < mine)
            return (false, null, $"The server offers {targetVersion}, older than this agent ({Program.Version}); update refused.");

        PrepareFolder();
        var file = Path.Combine(Folder, "ZentriqAgent-" + Safe(targetVersion) + ".msi");
        try { File.Delete(file); } catch (IOException) { }
        client.DownloadPackageAsync(file, ct).GetAwaiter().GetResult();

        string actual;
        using (var s = File.OpenRead(file))
        using (var h = SHA256.Create())
            actual = BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(file); } catch (IOException) { }
            return (false, null, "The downloaded package does not match the checksum from the server; update refused.");
        }

        var log = Path.Combine(Log.Directory, "update-" + Safe(targetVersion) + ".log");
        var started = $"Installing version {targetVersion}; the agent restarts and reports the new version at its next check-in.";
        // Reported before msiexec runs: Windows Installer stops this service soon after it starts.
        client.ReportActionAsync(actionId, true, "started", started, ct).GetAwaiter().GetResult();
        var msiexec = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\msiexec.exe");
        // A separate process: Windows Installer stops this service during the upgrade and msiexec carries on without it.
        var psi = new ProcessStartInfo(msiexec, $"/i \"{file}\" /qn /norestart REBOOT=ReallySuppress MSIRESTARTMANAGERCONTROL=Disable /l*v \"{log}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Folder,
        };
        using (var p = Process.Start(psi))
        {
            if (p is null) return (false, null, "Windows Installer could not be started.");
            // An immediate exit means msiexec refused to start (e.g. another installation is running): report it.
            if (p.WaitForExit(5000) && p.ExitCode != 0 && p.ExitCode != 3010)
                return (false, null, p.ExitCode == 1618
                    ? "Another installation is in progress on the device; try the update again later."
                    : $"Windows Installer failed with exit code {p.ExitCode}; see {log} on the device.");
        }
        Log.Info($"Updating the agent {Program.Version} -> {targetVersion} (Windows Installer log: {log}).");
        return (true, "started", started);
    }

    /// <summary>Removes downloaded packages of earlier updates (called once the agent runs the new version).</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var f in Directory.GetFiles(Folder, "ZentriqAgent-*.msi"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddMinutes(-30)) File.Delete(f);
        }
        catch (Exception ex) { Log.Warn("Could not remove old update packages: " + ex.Message); }
    }

    private static void PrepareFolder()
    {
        // SYSTEM and Administrators only, so nobody else can swap the package between the check and the install.
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (Directory.Exists(Folder)) Directory.SetAccessControl(Folder, sec);
        else Directory.CreateDirectory(Folder, sec);
    }

    private static string Safe(string v)
    {
        var chars = v.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '.')) chars[i] = '_';
        return new string(chars);
    }
}
