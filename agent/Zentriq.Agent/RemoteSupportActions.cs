using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Threading;

namespace Zentriq.Agent;

/// <summary>
/// Installs or removes the Zentriq Remote Support agent, requested in the console. The installer is downloaded from
/// this agent's own Zentriq server over the mutually authenticated agent connection and checked against the SHA-256
/// the server sent with the request; then it is run with -fullinstall or -fulluninstall.
/// </summary>
internal static class RemoteSupportActions
{
    public const string ServiceName = "ZentriqRemoteSupport";
    private static string Folder => Path.Combine(AgentPaths.Root, "RemoteSupport");

    public static (bool Ok, string? State, string Message) Run(ServerClient client, string operation, string? sha256, CancellationToken ct)
    {
        if (operation != "install" && operation != "uninstall") return (false, null, "Unknown remote support operation '" + operation + "'.");
        if (string.IsNullOrWhiteSpace(sha256) || sha256!.Trim().Length != 64) return (false, null, "The server did not send an installer checksum; refused.");
        if (operation == "uninstall" && !Installed()) return (true, "absent", "Zentriq Remote Support is not installed.");

        AgentUpdater.PrepareFolder(Folder);
        var file = Path.Combine(Folder, "ZentriqRemoteSupport.exe");
        try { File.Delete(file); } catch (IOException) { }
        try
        {
            client.DownloadRemoteAgentAsync(file, ct).GetAwaiter().GetResult();
            string actual;
            using (var s = File.OpenRead(file))
            using (var h = SHA256.Create())
                actual = BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return (false, null, "The downloaded installer does not match the checksum from the server; refused.");

            var psi = new ProcessStartInfo(file, operation == "install" ? "-fullinstall" : "-fulluninstall")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Folder,
            };
            using (var p = Process.Start(psi))
            {
                if (p is null) return (false, null, "The installer could not be started.");
                if (!p.WaitForExit((int)TimeSpan.FromMinutes(4).TotalMilliseconds))
                {
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    return (false, null, "The installer did not finish within 4 minutes.");
                }
            }
            // The result is decided by the service, not the exit code (which differs between agent versions).
            for (var i = 0; i < 30; i++)
            {
                var installed = Installed();
                if (operation == "install" && installed) return (true, "installed", "Zentriq Remote Support installed; the device appears online in Remote Support within a minute.");
                if (operation == "uninstall" && !installed) return (true, "removed", "Zentriq Remote Support removed.");
                Thread.Sleep(1000);
            }
            return operation == "install"
                ? (false, null, "The installer finished but the ZentriqRemoteSupport service is not present.")
                : (false, null, "The ZentriqRemoteSupport service is still present after uninstall.");
        }
        finally { try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    public static bool Installed() =>
        ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));
}
