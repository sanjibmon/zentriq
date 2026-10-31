using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Zentriq.AgentLinux;

/// <summary>Start / stop / restart a systemd service, as requested by an administrator in the console.</summary>
internal static class ServiceActions
{
    /// <summary>Services the console must not stop: the agent itself, remote access and the core of the system.</summary>
    private static readonly string[] Protected =
    {
        "zentriq-agent", "ssh", "sshd", "dbus", "dbus-broker", "systemd-journald", "systemd-logind", "systemd-udevd", "systemd-networkd",
        "systemd-resolved", "NetworkManager", "networking", "network", "wicked", "polkit", "auditd", "getty@tty1",
    };

    public static (bool Ok, string? State, string Message) Run(string kind, string name, string operation, TimeSpan wait)
    {
        if (kind != "service") return (false, null, "Unsupported action '" + kind + "'.");
        if (operation is not ("start" or "stop" or "restart")) return (false, null, "Unknown operation '" + operation + "'.");
        var unit = name.EndsWith(".service", StringComparison.Ordinal) ? name[..^8] : name;
        if (!Regex.IsMatch(unit, @"^[A-Za-z0-9@._:\-\\]+$")) return (false, null, "'" + name + "' is not a valid service name.");
        if (operation != "start" && Protected.Any(p => p.Equals(unit, StringComparison.OrdinalIgnoreCase)))
            return (false, null, "'" + unit + "' is protected and cannot be stopped from the console.");
        var load = Shell.Run("systemctl", ["show", "-p", "LoadState", "--value", unit + ".service"]).Output.Trim();
        if (load is "not-found" or "") return (false, null, "Service '" + unit + "' does not exist on this device.");
        if (load == "masked" && operation != "stop") return (false, State(unit), "The service is masked; unmask it first.");
        var r = Shell.Run("systemctl", [operation, unit + ".service"], wait);
        var state = State(unit);
        if (!r.Ok) return (false, state, $"systemctl {operation} {unit} failed: {Last(r.Text)}");
        if (operation != "stop" && state != "Running") return (false, state, $"The service did not stay running ({state}); see 'journalctl -u {unit}' on the device.");
        return (true, state, operation switch { "stop" => "Service stopped.", "restart" => "Service restarted.", _ => "Service started." });
    }

    /// <summary>Running / Stopped / Failed / Start Pending / Stop Pending (the names the console uses for Windows too).</summary>
    public static string State(string unit) => Shell.Run("systemctl", ["is-active", unit + ".service"]).Output.Trim() switch
    {
        "active" => "Running",
        "activating" or "reloading" => "Start Pending",
        "deactivating" => "Stop Pending",
        "failed" => "Failed",
        _ => "Stopped",
    };

    internal static string Last(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "no output";
}

/// <summary>
/// Removes one package, requested in the console. Refused when other packages depend on it (the package manager would
/// remove them too) and for packages the system or the agent needs.
/// </summary>
internal static class PackageActions
{
    private static readonly string[] Protected =
    {
        "zentriq-agent", "systemd", "systemd-sysv", "openssh-server", "openssh", "sudo", "bash", "coreutils", "glibc", "libc6", "dpkg", "rpm", "apt",
        "dnf", "yum", "zypper", "pacman", "pam", "libpam-modules", "util-linux", "openssl", "ca-certificates", "grub2", "grub-pc", "grub-efi-amd64",
        "shim", "shim-signed", "kmod", "udev", "dbus", "filesystem", "base-files", "setup", "passwd", "login", "shadow-utils", "selinux-policy",
    };

    public static (bool Ok, string? State, string Message) Uninstall(string package, string? version, TimeSpan timeout)
    {
        var name = package.Trim();
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9+._:\-]*$")) return (false, null, $"'{package}' is not a package name.");
        if (Protected.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase)) || name.StartsWith("linux-image", StringComparison.Ordinal) || name.StartsWith("kernel", StringComparison.Ordinal))
            return (false, null, $"'{name}' is needed by the system (or by the Zentriq Agent) and cannot be uninstalled from the console.");

        if (Shell.Exists("dpkg-query"))
        {
            var q = Shell.Run("dpkg-query", ["-W", "-f=${Version}\t${Status}", name]);
            if (q.Ok && q.Output.Contains("install ok installed"))
            {
                var installed = q.Output.Split('\t')[0];
                if (!string.IsNullOrEmpty(version) && version != installed) return (true, "uninstalled", $"'{name}' {version} is not installed (version {installed} is).");
                var sim = Shell.Run("apt-get", ["-s", "-o", "Debug::NoLocking=1", "remove", name], TimeSpan.FromMinutes(2));
                var removed = Regex.Matches(sim.Output, @"^Remv (\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
                var others = removed.Where(r => r != name && r.Split(':')[0] != name).ToList();
                if (!sim.Ok) return (false, null, $"apt cannot remove '{name}': {ServiceActions.Last(sim.Text)}");
                if (others.Count > 0) return (false, null, $"Removing '{name}' would also remove {string.Join(", ", others.Take(10))}{(others.Count > 10 ? ", …" : "")}; refused. Remove it on the device if that is intended.");
                return Result(name, Shell.Run("apt-get", ["-y", "-o", "DPkg::Lock::Timeout=120", "remove", name], timeout));
            }
            if (!Shell.Exists("rpm")) return (true, "uninstalled", $"'{name}' is not installed on this device (already removed).");
        }
        if (Shell.Exists("rpm"))
        {
            var q = Shell.Run("rpm", ["-q", "--qf", "%{VERSION}-%{RELEASE}", name]);
            if (!q.Ok) return (true, "uninstalled", $"'{name}' is not installed on this device (already removed).");
            if (!string.IsNullOrEmpty(version) && version != q.Output.Trim()) return (true, "uninstalled", $"'{name}' {version} is not installed (version {q.Output.Trim()} is).");
            var test = Shell.Run("rpm", ["-e", "--test", name]);
            if (!test.Ok)
            {
                var needed = Regex.Matches(test.Text, @"is needed by \(installed\) (\S+)").Select(m => m.Groups[1].Value).Distinct().ToList();
                return (false, null, needed.Count > 0 ? $"Other packages need '{name}' ({string.Join(", ", needed.Take(10))}); refused." : $"rpm cannot remove '{name}': {ServiceActions.Last(test.Text)}");
            }
            if (Shell.Exists("dnf")) return Result(name, Shell.Run("dnf", ["-y", "--setopt=clean_requirements_on_remove=False", "remove", name], timeout));
            if (Shell.Exists("zypper")) return Result(name, Shell.Run("zypper", ["--non-interactive", "remove", "--no-clean-deps", name], timeout));
            if (Shell.Exists("yum")) return Result(name, Shell.Run("yum", ["-y", "remove", name], timeout));
            return Result(name, Shell.Run("rpm", ["-e", name], timeout));
        }
        if (Shell.Exists("pacman"))
        {
            if (!Shell.Run("pacman", ["-Q", name]).Ok) return (true, "uninstalled", $"'{name}' is not installed on this device (already removed).");
            return Result(name, Shell.Run("pacman", ["-R", "--noconfirm", name], timeout));
        }
        return (false, null, "No supported package manager (dpkg, rpm, pacman) on this device.");
    }

    private static (bool, string?, string) Result(string name, Shell.Result r) =>
        r.Ok ? (true, "uninstalled", $"'{name}' was uninstalled.")
        : r.Code == -1 ? (false, null, r.Output)
        : (false, null, $"Uninstalling '{name}' failed (exit code {r.Code}): {ServiceActions.Last(r.Text)}");
}

/// <summary>The agent's own service: restart, disable, enable, stop with a code.</summary>
internal static class ServiceControl
{
    public static void RestartSelf()
    {
        StopGuard.Set(false);   // the agent sets the stop protection again when it starts
        Shell.Run("systemctl", ["--no-block", "restart", Paths.ServiceName + ".service"]);
    }

    public static void DisableSelf()
    {
        var p = Protection.Load();
        p.StoppedByCode = DateTime.UtcNow;
        if (p.Enabled) p.Save();
        Events.Record("control", "Agent service disabled from the Zentriq console; it stays off until 'sudo zentriq-agent --enable' on the device.");
        StopGuard.Set(false);
        Shell.Run("systemctl", ["disable", Paths.ServiceName + ".service", Paths.ServiceName + "-watchdog.timer"]);
        Shell.Run("systemctl", ["--no-block", "stop", Paths.ServiceName + "-watchdog.timer", Paths.ServiceName + ".service"]);
    }

    public static int Enable()
    {
        var p = Protection.Load();
        if (p.StoppedByCode is not null) { p.StoppedByCode = null; if (p.Enabled) p.Save(); }
        var r = Shell.Run("systemctl", ["enable", "--now", Paths.ServiceName + ".service", Paths.ServiceName + "-watchdog.timer"]);
        Console.WriteLine(r.Ok ? "The Zentriq Agent is enabled and running." : "systemctl: " + r.Text);
        return r.Ok ? 0 : 1;
    }

    /// <summary>zentriq-agent --stop &lt;code&gt;: stops the agent until the device restarts (or --enable).</summary>
    public static int StopWithCode(string? code)
    {
        var p = Protection.Load();
        if (p.Active && !p.Verify(code, out var error)) { Console.Error.WriteLine(error); return 1; }
        p = Protection.Load();
        p.StoppedByCode = DateTime.UtcNow;
        if (p.Enabled) { p.Save(); Events.Record("control", "Agent service stopped with a code on the device; it starts again when the device restarts, or with --enable."); }
        StopGuard.Set(false);
        var r = Shell.Run("systemctl", ["stop", Paths.ServiceName + ".service"], TimeSpan.FromSeconds(60));
        Console.WriteLine(r.Ok ? "The Zentriq Agent is stopped until the device restarts ('sudo zentriq-agent --enable' starts it now)." : "systemctl: " + r.Text);
        return r.Ok ? 0 : 1;
    }

    /// <summary>zentriq-agent --disable &lt;code&gt;: stops the agent and keeps it off (also after restarts) until --enable.</summary>
    public static int DisableWithCode(string? code)
    {
        var p = Protection.Load();
        if (p.Active && !p.Verify(code, out var error)) { Console.Error.WriteLine(error); return 1; }
        p = Protection.Load();
        p.StoppedByCode = DateTime.UtcNow;
        if (p.Enabled) { p.Save(); Events.Record("control", "Agent service disabled with a code on the device; it stays off until 'zentriq-agent --enable'."); }
        StopGuard.Set(false);
        Shell.Run("systemctl", ["disable", Paths.ServiceName + ".service", Paths.ServiceName + "-watchdog.timer"]);
        var r = Shell.Run("systemctl", ["stop", Paths.ServiceName + "-watchdog.timer", Paths.ServiceName + ".service"], TimeSpan.FromSeconds(60));
        Console.WriteLine(r.Ok ? "The Zentriq Agent is disabled ('sudo zentriq-agent --enable' starts it again)." : "systemctl: " + r.Text);
        return r.Ok ? 0 : 1;
    }

    /// <summary>
    /// Every 5 minutes (systemd timer): with protection on, an agent service that was disabled or stopped without a code
    /// is enabled and started again, and the attempt is reported.
    /// </summary>
    public static int Watchdog()
    {
        if (!Shell.IsRoot || !Shell.Systemd) return 0;
        var p = Protection.Load();
        if (!p.Active || p.StoppedByCode is not null) return 0;
        var unit = Paths.ServiceName + ".service";
        var enabled = Shell.Run("systemctl", ["is-enabled", unit]).Output.Trim();
        if (enabled is "disabled" or "masked")
        {
            if (enabled == "masked") Shell.Run("systemctl", ["unmask", unit]);
            Shell.Run("systemctl", ["enable", unit]);
            Events.Record("tamper", $"The agent service was {enabled} without a code; it was enabled again.");
        }
        if (Shell.Run("systemctl", ["is-active", unit]).Output.Trim() is not ("active" or "activating"))
        {
            Shell.Run("systemctl", ["start", unit], TimeSpan.FromSeconds(60));
            Events.Record("tamper", "The agent service was stopped without a code; it was started again.");
        }
        StopGuard.Sync(p);
        return 0;
    }
}

/// <summary>
/// Installs or removes Zentriq Remote Support (the remote support engine's Linux agent), requested in the console. The
/// agent binary comes from this agent's own Zentriq server over the mutually authenticated connection and is checked
/// against the SHA-256 the server sent with the request.
/// </summary>
internal static class RemoteSupportActions
{
    private const string Root = "/usr/local/mesh_services";

    public static (bool Ok, string? State, string Message) Run(ServerClient client, string operation, string? sha256, CancellationToken ct)
    {
        if (operation is not ("install" or "uninstall")) return (false, null, "Unknown remote support operation '" + operation + "'.");
        if (operation == "uninstall" && !Installed()) return (true, "absent", "Zentriq Remote Support is not installed.");
        if (operation == "uninstall" && InstalledBinary() is { } installedBinary)
        {
            Shell.Run(installedBinary, ["-fulluninstall"], TimeSpan.FromMinutes(3));
            return Wait(false) ? (true, "removed", "Zentriq Remote Support removed.") : (false, null, "Zentriq Remote Support is still installed after uninstall.");
        }
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Trim().Length != 64) return (false, null, "The server did not send an installer checksum; refused.");
        Directory.CreateDirectory(Paths.RemoteSupport);
        var file = Path.Combine(Paths.RemoteSupport, "meshagent");
        try { File.Delete(file); } catch (IOException) { }
        try
        {
            client.DownloadRemoteAgentAsync(file, ct).GetAwaiter().GetResult();
            if (!string.Equals(Sha256(file), sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return (false, null, "The downloaded remote support agent does not match the checksum from the server; refused.");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var r = Shell.Run(file, [operation == "install" ? "-install" : "-fulluninstall"], TimeSpan.FromMinutes(4));
            if (r.Code == -1) return (false, null, "The remote support installer did not finish within 4 minutes.");
            var want = operation == "install";
            return Wait(want)
                ? (true, want ? "installed" : "removed", want ? "Zentriq Remote Support installed; the device appears online in Remote Support within a minute." : "Zentriq Remote Support removed.")
                : (false, null, want ? $"The installer finished but Zentriq Remote Support is not running: {ServiceActions.Last(r.Text)}" : "Zentriq Remote Support is still installed after uninstall.");
        }
        finally { try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private static bool Wait(bool installed)
    {
        for (var i = 0; i < 30; i++)
        {
            if (Installed() == installed) return true;
            Thread.Sleep(1000);
        }
        return false;
    }

    public static string? InstalledBinary() =>
        Directory.Exists(Root) ? Directory.EnumerateFiles(Root, "meshagent", SearchOption.AllDirectories).FirstOrDefault() : null;

    /// <summary>Installed: its service is registered with systemd (unit files that start an agent under /usr/local/mesh_services).</summary>
    public static bool Installed()
    {
        if (InstalledBinary() is null) return false;
        foreach (var dir in new[] { "/etc/systemd/system", "/lib/systemd/system", "/usr/lib/systemd/system" })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.service"))
                try { if (File.ReadAllText(f).Contains(Root, StringComparison.Ordinal)) return true; } catch (IOException) { }
        }
        return !Shell.Systemd;
    }

    internal static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}

/// <summary>
/// Agent update requested in the console (or automatic). The package for this device (deb, rpm or tar.gz, as it was
/// installed) is downloaded from the agent's own server over the authenticated connection, checked against the
/// SHA-256 sent with the request, and installed by a separate transient systemd unit (the package manager restarts this
/// service). Enrollment, certificate and settings in /var/lib/zentriq-agent are kept.
/// </summary>
internal static class AgentUpdater
{
    public static string InstalledFormat()
    {
        try { var f = File.ReadAllText(Paths.InstalledBy).Trim(); if (f is "deb" or "rpm" or "tar") return f; } catch (IOException) { }
        return Shell.Exists("dpkg-query") && Shell.Run("dpkg-query", ["-W", Paths.ServiceName]).Ok ? "deb"
             : Shell.Exists("rpm") && Shell.Run("rpm", ["-q", Paths.ServiceName]).Ok ? "rpm" : "tar";
    }

    /// <summary>"deb=&lt;sha&gt;;rpm=&lt;sha&gt;;tar=&lt;sha&gt;" → the checksum for one format.</summary>
    public static string? ChecksumFor(string? argument, string format)
    {
        foreach (var part in (argument ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() == format && kv[1].Trim().Length == 64) return kv[1].Trim().ToLowerInvariant();
        }
        return null;
    }

    public static (bool Ok, string? State, string Message) Start(ServerClient client, string actionId, string targetVersion, string? argument, CancellationToken ct)
    {
        if (string.Equals(targetVersion, AgentVersion.Current, StringComparison.OrdinalIgnoreCase)) return (true, "current", $"The agent is already version {AgentVersion.Current}.");
        if (System.Version.TryParse(targetVersion, out var target) && System.Version.TryParse(AgentVersion.Current, out var mine) && target < mine)
            return (false, null, $"The server offers {targetVersion}, older than this agent ({AgentVersion.Current}); update refused.");
        var format = InstalledFormat();
        var sha = ChecksumFor(argument, format);
        if (sha is null) return (false, null, $"The server has no {format} package checksum for {Platform.Id}; update refused (the server may not have the Linux agent packages).");

        Paths.EnsureData();
        var file = Path.Combine(Paths.Updates, $"zentriq-agent-{Safe(targetVersion)}.{(format == "tar" ? "tar.gz" : format)}");
        try { File.Delete(file); } catch (IOException) { }
        client.DownloadPackageAsync(file, format, ct).GetAwaiter().GetResult();
        if (!string.Equals(RemoteSupportActions.Sha256(file), sha, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(file); } catch (IOException) { }
            return (false, null, "The downloaded package does not match the checksum from the server; update refused.");
        }
        var log = Path.Combine(Paths.Logs, $"update-{Safe(targetVersion)}.log");
        var install = format switch
        {
            "deb" => $"dpkg --force-confold -i '{file}'",
            "rpm" => $"rpm -U --replacepkgs '{file}'",
            _ => $"d=$(mktemp -d) && tar -xzf '{file}' -C \"$d\" && \"$d\"/zentriq-agent/install.sh --upgrade; r=$?; rm -rf \"$d\"; exit $r",
        };
        var started = $"Installing version {targetVersion} ({format}); the agent restarts and reports the new version at its next check-in.";
        client.ReportActionAsync(actionId, true, "started", started, ct).GetAwaiter().GetResult();
        StopGuard.Set(false);
        // Outside this service's control group: the package manager restarts the agent while it runs.
        var script = $"sleep 2; ({install}) >'{log}' 2>&1; echo \"exit $?\" >>'{log}'; rm -f '{file}'";
        var r = Shell.Exists("systemd-run") && Shell.Systemd
            ? Shell.Run("systemd-run", ["--unit=zentriq-agent-update-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), "--collect", "--quiet", "/bin/sh", "-c", script])
            : Shell.Run("/bin/sh", ["-c", $"setsid /bin/sh -c \"{script.Replace("\"", "\\\"")}\" >/dev/null 2>&1 &"]);
        if (!r.Ok) return (false, null, "The update could not be started: " + r.Text);
        Log.Info($"Updating the agent {AgentVersion.Current} -> {targetVersion} ({format}; log: {log}).");
        return (true, "started", started);
    }

    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Paths.Updates)) return;
            foreach (var f in Directory.GetFiles(Paths.Updates, "zentriq-agent-*"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddMinutes(-30)) File.Delete(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Could not remove old update packages: " + ex.Message); }
    }

    private static string Safe(string v) => new(v.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
}
