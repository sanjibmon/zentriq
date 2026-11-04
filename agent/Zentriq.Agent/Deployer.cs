using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace Zentriq.Agent;

/// <summary>
/// Push installation from Software Deployment: downloads the package from this agent's Zentriq server over the
/// mutually authenticated agent connection, checks its SHA-256 against the one the server sent with the request, and
/// runs it silently as SYSTEM (Windows Installer: msiexec /i … /qn; setup programs with the switches set on the
/// package). Uninstall: msiexec /x for Windows Installer packages, otherwise the software's own silent uninstaller
/// registered on the device. Installations the agent runs are not stopped by application control.
/// </summary>
internal static class Deployer
{
    private static string Root => Path.Combine(AgentPaths.Root, "Deploy");
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(60);

    public static (bool Ok, string? State, string Message) Run(ServerClient client, AgentAction a, CancellationToken ct)
    {
        Dictionary<string, JsonElement> p;
        try { p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.Argument ?? "{}") ?? new Dictionary<string, JsonElement>(); }
        catch (JsonException) { return (false, null, "The deployment request is not valid."); }
        string? S(string k) => p.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var name = S("name") ?? "the package";
        var label = name + (S("version") is { } ver ? " " + ver : "");
        var format = S("format") ?? "";
        if (format is not ("msi" or "exe")) return (false, null, $"This package ({format}) is not for Windows.");

        if (a.Operation == "uninstall" && format == "exe")
        {
            var (ok, st, msg) = SoftwareActions.Uninstall(S("detectName") ?? name, null, Timeout);
            return (ok, ok ? "uninstalled" : st, msg);
        }

        var sha = S("sha256");
        if (string.IsNullOrWhiteSpace(sha) || sha!.Length != 64) return (false, null, "The server did not send the package checksum; refused.");
        var dir = Path.Combine(Root, a.Target);
        AgentUpdater.PrepareFolder(Root);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, SafeName(S("fileName") ?? (a.Target + "." + format)));
        try
        {
            if (!File.Exists(file) || !string.Equals(Sha256(file), sha, StringComparison.OrdinalIgnoreCase))
            {
                client.DownloadDeploymentAsync(a.Target, file, ct).GetAwaiter().GetResult();
                if (!string.Equals(Sha256(file), sha, StringComparison.OrdinalIgnoreCase))
                    return (false, null, "The downloaded package does not match the checksum from the server; nothing was installed.");
            }
            var log = Path.Combine(dir, a.Operation + ".log");
            string exe, args;
            if (format == "msi")
            {
                exe = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
                var extra = a.Operation == "uninstall" ? S("uninstallArgs") ?? "/qn /norestart" : S("installArgs") ?? "/qn /norestart";
                if (extra.IndexOf("/q", StringComparison.OrdinalIgnoreCase) < 0) extra += " /qn";
                args = $"{(a.Operation == "uninstall" ? "/x" : "/i")} \"{file}\" {extra} /l*v \"{log}\"";
            }
            else
            {
                exe = file;
                args = S("installArgs") ?? "";
            }
            Log.Info($"Deployment: {a.Operation} {label} ({exe} {args}).");
            using var proc = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir })!;
            if (!proc.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { proc.Kill(); } catch (Exception) { }
                return (false, null, $"The {(format == "msi" ? "Windows Installer package" : "setup program")} did not finish within {Timeout.TotalMinutes:0} minutes and was stopped.");
            }
            var code = proc.ExitCode;
            AgentService.RequestInventory();
            var done = a.Operation == "uninstall" ? "uninstalled" : "installed";
            return code switch
            {
                0 => (true, done, $"{label} {done}."),
                3010 or 1641 => (true, done, $"{label} {done}; Windows needs a restart to finish."),
                1605 when a.Operation == "uninstall" => (true, done, $"{label} was not installed."),
                1618 => (false, null, "Another installation is in progress on the device; retry the deployment later."),
                1603 => (false, null, $"The installation of {label} failed (Windows Installer error 1603). See {log} on the device."),
                1638 => (true, done, $"{label} (or a newer version) is already installed."),
                _ => (false, null, $"{(format == "msi" ? "msiexec" : "The setup program")} ended with exit code {code}" + (format == "exe" && code == 1 ? " — check the silent switches set on the package." : ".")),
            };
        }
        finally
        {
            // The package is not kept after a successful run (the next deployment downloads it again).
            try { File.Delete(file); } catch (Exception) { }
        }
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 120 ? name.Substring(name.Length - 120) : name;
    }

    private static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        using var h = SHA256.Create();
        return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
    }
}
