using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Zentriq.Inventory;

namespace Zentriq.AgentLinux;

/// <summary>
/// The same agent program runs on Linux and on macOS (build 144). On macOS it is a launchd daemon
/// (/Library/LaunchDaemons/com.zentriq.agent.plist) installed from a .pkg, and it does inventory, the live
/// connection, scheduled check-ins, collect-now and self-update. Actions that need Linux tools (package managers,
/// systemd, application control, patching, scripts, software metering) are not offered on macOS: the agent does not
/// announce those capabilities, so the console does not send them.
/// </summary>
internal static class Os
{
    public static readonly bool Mac = OperatingSystem.IsMacOS();
    public static string Name => Mac ? "macOS" : "Linux";
    public const string LaunchdLabel = "com.zentriq.agent";
    public const string LaunchdPlist = "/Library/LaunchDaemons/com.zentriq.agent.plist";

    /// <summary>What a Mac agent can do (the console only offers these).</summary>
    public static readonly string[] MacCapabilities = ["self-update", "schedule", "presence", "macos"];
}

/// <summary>macOS inventory: the read-only commands Zentriq uses over SSH, plus application versions and security settings, run as root.</summary>
internal static class MacCollector
{
    private static readonly Dictionary<string, string> Extra = new()
    {
        // Applications with their versions (system_profiler reads every bundle's Info.plist).
        ["appsjson"] = "system_profiler -json -detailLevel mini SPApplicationsDataType 2>/dev/null",
        ["filevault"] = "fdesetup status 2>/dev/null",
        ["firewall"] = "/usr/libexec/ApplicationFirewall/socketfilterfw --getglobalstate 2>/dev/null",
        ["gatekeeper"] = "spctl --status 2>/dev/null",
        ["sip"] = "csrutil status 2>/dev/null",
        ["admins"] = "dscl . -read /Groups/admin GroupMembership 2>/dev/null",
        ["computername"] = "scutil --get ComputerName 2>/dev/null",
    };

    public static InventoryDocument Collect()
    {
        var commands = UnixCollector.MacCommands.Concat(Extra).ToList();
        var outputs = new ConcurrentDictionary<string, string>();
        var errors = new ConcurrentBag<string>();
        Parallel.ForEach(commands, new ParallelOptions { MaxDegreeOfParallelism = 4 }, c =>
        {
            var timeout = TimeSpan.FromSeconds(c.Key == "appsjson" ? 180 : 45);
            var r = Shell.Run("/usr/bin/nice", ["-n", "10", "/bin/sh", "-c", c.Value], timeout);
            if (r.Code == -1) errors.Add($"{c.Key}: {r.Output}");
            outputs[c.Key] = r.Code < 0 ? "" : r.Output;
        });
        var arch = Shell.Run("uname", ["-m"]).Output.Trim();
        var doc = UnixCollector.ParseMac(outputs, $"Darwin\n{arch}\n", AgentVersion.Current);
        doc.Source = "agent";
        doc.Errors.RemoveAll(e => e.StartsWith("software:", StringComparison.Ordinal));
        doc.Errors.AddRange(errors);

        var apps = ParseApplications(outputs.GetValueOrDefault("appsjson") ?? "");
        if (apps.Count > 0) { doc.Software.Clear(); doc.Software.AddRange(apps); }
        else doc.Errors.Add("software: system_profiler did not list the applications; only names from /Applications are shown.");

        var fv = outputs.GetValueOrDefault("filevault") ?? "";
        if (fv.Contains("FileVault is On", StringComparison.OrdinalIgnoreCase)) doc.Security.DiskEncryption = true;
        else if (fv.Contains("FileVault is Off", StringComparison.OrdinalIgnoreCase)) doc.Security.DiskEncryption = false;
        if (doc.Security.DiskEncryption is { } enc) foreach (var v in doc.Hardware.Volumes.Where(v => v.Name == "/")) v.Encrypted = enc;
        var fw = outputs.GetValueOrDefault("firewall") ?? "";
        if (fw.Contains("enabled", StringComparison.OrdinalIgnoreCase) || fw.Contains("State = 1", StringComparison.Ordinal) || fw.Contains("State = 2", StringComparison.Ordinal)) doc.Security.FirewallEnabled = true;
        else if (fw.Contains("disabled", StringComparison.OrdinalIgnoreCase) || fw.Contains("State = 0", StringComparison.Ordinal)) doc.Security.FirewallEnabled = false;
        // System Integrity Protection plays the role of Secure Boot in the console's security posture.
        var sip = outputs.GetValueOrDefault("sip") ?? "";
        if (sip.Contains("enabled", StringComparison.OrdinalIgnoreCase)) doc.Security.SecureBoot = true;
        else if (sip.Contains("disabled", StringComparison.OrdinalIgnoreCase)) doc.Security.SecureBoot = false;

        var admins = outputs.GetValueOrDefault("admins") ?? "";
        var i = admins.IndexOf(':');
        if (i > 0) doc.Users.LocalAdmins = admins[(i + 1)..].Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Where(a => a != "root").Distinct().ToList();
        if (doc.Users.LoggedOn is "root" or "_mbsetupuser") doc.Users.LoggedOn = null;
        return doc;
    }

    /// <summary>Applications from system_profiler's JSON (name, version, publisher from the signature, where it came from).</summary>
    internal static List<SoftwareItem> ParseApplications(string json)
    {
        var list = new List<SoftwareItem>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var d = JsonDocument.Parse(json);
            if (!d.RootElement.TryGetProperty("SPApplicationsDataType", out var apps) || apps.ValueKind != JsonValueKind.Array) return list;
            foreach (var a in apps.EnumerateArray())
            {
                var name = Str(a, "_name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var path = Str(a, "path") ?? "";
                // Apple's own helpers inside system folders are not applications people install.
                if (path.StartsWith("/System/Library/", StringComparison.Ordinal) || path.StartsWith("/Library/Apple/", StringComparison.Ordinal)) continue;
                var signedBy = a.TryGetProperty("signed_by", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0 ? s[0].GetString() : null;
                var publisher = signedBy is null ? null
                    : signedBy.StartsWith("Developer ID Application: ", StringComparison.Ordinal) ? signedBy["Developer ID Application: ".Length..].Split(" (")[0]
                    : signedBy.StartsWith("Software Signing", StringComparison.Ordinal) || signedBy.StartsWith("Apple ", StringComparison.Ordinal) ? "Apple"
                    : signedBy.StartsWith("Apple Mac OS Application Signing", StringComparison.Ordinal) ? null : signedBy;
                DateTimeOffset? modified = DateTimeOffset.TryParse(Str(a, "lastModified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m : null;
                list.Add(new SoftwareItem
                {
                    Name = name.Trim(),
                    Version = Str(a, "version")?.Trim(),
                    Publisher = publisher,
                    InstallDate = modified,
                    Architecture = Str(a, "arch_kind") switch { "arch_arm_i64" => "universal", "arch_arm" => "arm64", "arch_i64" => "x64", var k => k },
                    Source = Str(a, "obtained_from") == "mac_app_store" ? "appstore" : "applications",
                });
            }
        }
        catch (JsonException) { }
        return list.GroupBy(x => (x.Name, x.Version)).Select(g => g.First()).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static (string Host, string Os) HostFacts()
    {
        var host = Shell.Run("scutil", ["--get", "LocalHostName"]).Output.Trim();
        if (host.Length == 0) host = Environment.MachineName.Split('.')[0];
        var name = Shell.Run("sw_vers", ["-productName"]).Output.Trim();
        var ver = Shell.Run("sw_vers", ["-productVersion"]).Output.Trim();
        return (host, $"{(name.Length > 0 ? name : "macOS")} {ver}".Trim());
    }
}

/// <summary>The launchd daemon on macOS: restart (kickstart), stop and start.</summary>
internal static class MacService
{
    public static void Restart() =>
        Shell.Run("/bin/sh", ["-c", $"(sleep 1; /bin/launchctl kickstart -k system/{Os.LaunchdLabel}) >/dev/null 2>&1 &"]);

    public static bool Loaded => Shell.Run("/bin/launchctl", ["print", "system/" + Os.LaunchdLabel]).Ok;

    public static int Stop()
    {
        var r = Shell.Run("/bin/launchctl", ["bootout", "system/" + Os.LaunchdLabel], TimeSpan.FromSeconds(60));
        Console.WriteLine(r.Ok ? "The Zentriq Agent is stopped until the Mac restarts ('sudo zentriq-agent --enable' starts it now)." : "launchctl: " + r.Text);
        return r.Ok ? 0 : 1;
    }

    public static int Start()
    {
        Shell.Run("/bin/launchctl", ["enable", "system/" + Os.LaunchdLabel]);
        var r = Loaded ? Shell.Run("/bin/launchctl", ["kickstart", "-k", "system/" + Os.LaunchdLabel]) : Shell.Run("/bin/launchctl", ["bootstrap", "system", Os.LaunchdPlist]);
        Console.WriteLine(r.Ok ? "The Zentriq Agent is enabled and running." : "launchctl: " + r.Text);
        return r.Ok ? 0 : 1;
    }

    /// <summary>Installs a downloaded .pkg outside this process (the package's scripts restart the daemon).</summary>
    public static Shell.Result InstallPackage(string file, string log) =>
        Shell.Run("/bin/sh", ["-c", $"(sleep 2; /usr/sbin/installer -pkg '{file}' -target / >'{log}' 2>&1; echo \"exit $?\" >>'{log}'; rm -f '{file}') >/dev/null 2>&1 &"]);
}
