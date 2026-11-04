using System.Security.Cryptography;
using System.Text.Json;

namespace Zentriq.AgentLinux;

/// <summary>
/// Tamper protection for the Zentriq Remote Support agent on Linux, like the Zentriq Agent's own: while protection is on
/// (and Software Deployment › Settings › "Protect the remote support agent"), "systemctl stop" of its service is refused
/// (systemd drop-in, RefuseManualStop) and systemd restarts it when it ends; every five minutes the agent enables and
/// starts it again if it was disabled, masked or stopped, and reports when it was removed (the server then installs it
/// again). Removing it from the console lifts the protection first.
/// </summary>
internal static class RemoteGuard
{
    private static string Marker => Path.Combine(Paths.Data, "remote-support-protected");
    private static string? _reportedMissing;

    /// <summary>The systemd unit of the remote support agent (the unit file that starts an agent under /usr/local/mesh_services).</summary>
    public static string? Unit()
    {
        foreach (var dir in new[] { "/etc/systemd/system", "/lib/systemd/system", "/usr/lib/systemd/system" })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.service"))
                try { if (File.ReadAllText(f).Contains("/usr/local/mesh_services", StringComparison.Ordinal)) return Path.GetFileNameWithoutExtension(f); }
                catch (IOException) { }
        }
        return null;
    }

    private static string DropIn(string unit) => $"/etc/systemd/system/{unit}.service.d/10-zentriq-protection.conf";

    private static bool Wanted => Protection.Load().Active && AppControlAgent.Policy.ProtectRemoteAgent;

    public static string State
    {
        get
        {
            var unit = Unit();
            if (unit is null || !RemoteSupportActions.Installed()) return File.Exists(Marker) ? "missing" : "absent";
            return File.Exists(DropIn(unit)) ? "on" : "off";
        }
    }

    public static void Sync()
    {
        if (!Shell.IsRoot || !Shell.Systemd) return;
        try
        {
            var unit = Unit();
            if (unit is null || !RemoteSupportActions.Installed())
            {
                if (File.Exists(Marker) && Wanted && _reportedMissing != "yes")
                {
                    _reportedMissing = "yes";
                    Events.Record("tamper", "The protected remote support agent was removed from this device. Zentriq installs it again.");
                }
                return;
            }
            _reportedMissing = null;
            var dropIn = DropIn(unit);
            if (!Wanted)
            {
                if (File.Exists(dropIn)) { File.Delete(dropIn); Shell.Run("systemctl", ["daemon-reload"], TimeSpan.FromSeconds(60)); Log.Info("Remote support agent protection is off."); }
                try { File.Delete(Marker); } catch (IOException) { }
                return;
            }
            const string content = "# Zentriq tamper protection for the remote support agent (managed by the Zentriq Agent).\n[Unit]\nRefuseManualStop=yes\n[Service]\nRestart=always\nRestartSec=10\n";
            if (!File.Exists(dropIn) || File.ReadAllText(dropIn) != content)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dropIn)!);
                File.WriteAllText(dropIn, content);
                Shell.Run("systemctl", ["daemon-reload"], TimeSpan.FromSeconds(60));
                Log.Info($"Remote support agent ({unit}) protected: 'systemctl stop' is refused and it is started again when it ends.");
            }
            File.WriteAllText(Marker, DateTime.UtcNow.ToString("o"));
            var changed = new List<string>();
            var enabled = Shell.Run("systemctl", ["is-enabled", unit + ".service"]).Output.Trim();
            if (enabled is "masked" or "masked-runtime") { changed.Add("it was masked"); Shell.Run("systemctl", ["unmask", unit + ".service"]); }
            if (enabled is "disabled" or "masked" or "masked-runtime") { changed.Add("it was disabled"); Shell.Run("systemctl", ["enable", unit + ".service"]); }
            if (Shell.Run("systemctl", ["is-active", unit + ".service"]).Output.Trim() is not ("active" or "activating" or "reloading"))
            {
                changed.Add("it was stopped");
                Shell.Run("systemctl", ["start", unit + ".service"], TimeSpan.FromSeconds(60));
            }
            if (changed.Count > 0) Events.Record("tamper", "Someone changed the protected remote support agent: " + string.Join("; ", changed) + ". Restored.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Remote support protection: " + ex.Message); }
    }

    /// <summary>Before the console removes or reinstalls the remote support agent.</summary>
    public static void Release()
    {
        try
        {
            File.Delete(Marker);
            if (Unit() is { } unit && File.Exists(DropIn(unit)))
            {
                File.Delete(DropIn(unit));
                Shell.Run("systemctl", ["daemon-reload"], TimeSpan.FromSeconds(60));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Remote support protection: " + ex.Message); }
    }
}

/// <summary>
/// Push installation from Software Deployment on Linux: the .deb or .rpm package is downloaded from this agent's own
/// server over the authenticated connection, checked against the SHA-256 sent with the request, and installed with the
/// package manager (apt, dnf, zypper or yum, which also install its dependencies). Uninstall removes the package by name.
/// </summary>
internal static class Deployer
{
    private static string Root => Path.Combine(Paths.Data, "deploy");

    public static (bool Ok, string? State, string Message) Run(ServerClient client, AgentAction a, CancellationToken ct)
    {
        Dictionary<string, JsonElement> p;
        try { p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.Argument ?? "{}") ?? new(); }
        catch (JsonException) { return (false, null, "The deployment request is not valid."); }
        string? S(string k) => p.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var name = S("name") ?? "the package";
        var label = name + (S("version") is { } ver ? " " + ver : "");
        var format = S("format") ?? "";
        if (format is not ("deb" or "rpm")) return (false, null, $"This package ({format}) is not for Linux.");
        if (format == "deb" && !Shell.Exists("dpkg")) return (false, null, "This is a .deb package; this device uses RPM packages.");
        if (format == "rpm" && !Shell.Exists("rpm")) return (false, null, "This is an .rpm package; this device uses .deb packages.");

        if (a.Operation == "uninstall")
        {
            var (ok, st, msg) = PackageActions.Uninstall(S("detectName") ?? name, null, TimeSpan.FromMinutes(30));
            return (ok, ok ? "uninstalled" : st, msg);
        }

        var sha = S("sha256");
        if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64) return (false, null, "The server did not send the package checksum; refused.");
        Directory.CreateDirectory(Root);
        var file = Path.Combine(Root, $"{a.Target}.{format}");
        try
        {
            client.DownloadDeploymentAsync(a.Target, file, ct).GetAwaiter().GetResult();
            if (!string.Equals(RemoteSupportActions.Sha256(file), sha, StringComparison.OrdinalIgnoreCase))
                return (false, null, "The downloaded package does not match the checksum from the server; nothing was installed.");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            // The package managers the agent runs itself are not checked by application control.
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            Directory.CreateDirectory(AppControlAgent.Folder);
            Paths.WriteSecret(AppControlAgent.DeployToken, System.Text.Encoding.UTF8.GetBytes(token));
            var env = new Dictionary<string, string> { ["ZENTRIQ_DEPLOY_TOKEN"] = token };
            Log.Info($"Deployment: installing {label} ({format}).");
            var timeout = TimeSpan.FromMinutes(60);
            var r = format == "deb"
                ? Shell.Run("apt-get", ["install", "-y", "-q", "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold", file], timeout, env)
                : Shell.Exists("dnf") ? Shell.Run("dnf", ["install", "-y", "-q", file], timeout, env)
                : Shell.Exists("zypper") ? Shell.Run("zypper", ["--non-interactive", "install", "--allow-unsigned-rpm", file], timeout, env)
                : Shell.Exists("yum") ? Shell.Run("yum", ["install", "-y", "-q", file], timeout, env)
                : Shell.Run("rpm", ["-U", "--replacepkgs", file], timeout, env);
            try { File.Delete(AppControlAgent.DeployToken); } catch (IOException) { }
            AgentService.RequestInventory();
            if (r.Code == -1) return (false, null, $"The installation of {label} did not finish within 60 minutes.");
            return r.Ok ? (true, "installed", $"{label} installed.") : (false, null, $"The installation of {label} failed: {ServiceActions.Last(r.Text)}");
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }
}
