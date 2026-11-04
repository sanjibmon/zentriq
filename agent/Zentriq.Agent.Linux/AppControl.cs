using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Zentriq.Inventory;

namespace Zentriq.AgentLinux;

/// <summary>
/// Application control on Linux: the cached policy (approved software, mode, message), package-manager hooks that check
/// every package before it is installed, events and requests waiting to be sent. Hooks: dpkg (Debian, Ubuntu and
/// relatives; also covers apt, which installs through dpkg), dnf (RHEL 8 / 9 / 10, Rocky, AlmaLinux, Oracle Linux,
/// CentOS Stream, Amazon Linux 2023) and zypper (SUSE, openSUSE). A package that is not approved is reported (audit) or
/// refused with the organisation's message (block); the user can ask for approval with "zentriq-agent --request".
/// Updates of installed packages, kernels, firmware and libraries are always allowed. Software installed another way
/// (snap, flatpak, pip, copied files, "rpm -i") is reported by the inventory check on the server.
/// </summary>
internal static class AppControlAgent
{
    private static readonly object Gate = new();
    public static string Folder => Path.Combine(Paths.Data, "appcontrol");
    private static string PolicyFile => Path.Combine(Folder, "policy.json");
    /// <summary>Events written by the package-manager hooks (separate processes, root).</summary>
    public static string Spool => Path.Combine(Folder, "spool");
    /// <summary>Requests from users (zentriq-agent --request, any user): write-only for others (1733).</summary>
    public const string RequestSpool = "/var/spool/zentriq-agent/requests";
    /// <summary>Set by the agent for the package managers it runs itself (deployments): those installations are not checked.</summary>
    public static string DeployToken => Path.Combine(Folder, "deploy-token");
    private static long _seenRevision = -1;
    private static FileSystemWatcher? _spoolWatch, _requestWatch;

    public static AppControlPolicy Policy
    {
        get
        {
            try { return File.Exists(PolicyFile) ? JsonSerializer.Deserialize<AppControlPolicy>(File.ReadAllText(PolicyFile)) ?? new() : new(); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new(); }
        }
    }

    /// <summary>The spool folders (events from the hooks; requests from any user: write-only, sticky).</summary>
    public static void PrepareSpools()
    {
        Directory.CreateDirectory(Spool);
        Directory.CreateDirectory(RequestSpool);
        File.SetUnixFileMode(RequestSpool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                           | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.StickyBit);
        File.SetUnixFileMode("/var/spool/zentriq-agent", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    /// <summary>The service started: hooks and the remote support agent's protection as the saved policy says; watch the spools.</summary>
    public static void OnServiceStart()
    {
        if (!Shell.IsRoot) return;
        try
        {
            PrepareSpools();
            Apply(Policy);
            _spoolWatch = Watch(Spool);
            _requestWatch = Watch(RequestSpool);
        }
        catch (Exception ex) { Log.Warn("Application control: " + ex.Message); }
    }

    private static FileSystemWatcher? Watch(string dir)
    {
        try
        {
            var w = new FileSystemWatcher(dir) { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.FileName };
            w.Created += (_, _) => AgentService.RequestServerWake();
            w.Renamed += (_, _) => AgentService.RequestServerWake();
            return w;
        }
        catch (Exception) { return null; }
    }

    public static void Sync(ServerClient client, AppControlInfo? info, CancellationToken ct)
    {
        if (!Shell.IsRoot) return;
        if (info is not null && info.Revision != Policy.Revision)
        {
            lock (Gate) _seenRevision = info.Revision;
            try
            {
                var json = client.GetAppControlPolicyAsync(ct).GetAwaiter().GetResult();
                var p = JsonSerializer.Deserialize<AppControlPolicy>(json) ?? new();
                var old = Policy;
                Directory.CreateDirectory(Folder);
                Paths.WriteSecret(PolicyFile, Encoding.UTF8.GetBytes(json));
                // Readable by the hooks only (root): they run as root under the package manager.
                Log.Info($"Application control policy {p.Revision}: {p.Mode}, {p.Rules.Count} rule(s)" + (old.Mode != p.Mode ? $" (was {old.Mode})" : "") + ".");
                Apply(p);
                AgentService.RequestServerWake();   // report the applied policy at once (the console shows it as current)
            }
            catch (Exception ex) { Log.Warn("Application control: could not fetch the policy: " + AgentWorker.Describe(ex)); }
        }
        Flush(client, ct);
    }

    private static void Apply(AppControlPolicy p)
    {
        if (p.Mode == "off") PackageHooks.Remove(); else PackageHooks.Install();
        RemoteGuard.Sync();
    }

    public static Dictionary<string, object?>? Report()
    {
        if (!Shell.IsRoot) return null;
        var p = Policy;
        return new Dictionary<string, object?>
        {
            ["revision"] = Math.Max(_seenRevision, p.Revision),
            ["mode"] = p.Mode == "off" ? "off" : PackageHooks.Installed ? p.Mode : "error",
            ["wdac"] = null,
            ["remoteProtection"] = RemoteGuard.State,
        };
    }

    // ---- events and requests --------------------------------------------------------------------------------

    /// <summary>Written by a hook (a separate process): one file per event, picked up by the service.</summary>
    public static void SpoolEvent(string kind, SoftwareCandidate c, string? user, string detail)
    {
        try
        {
            Directory.CreateDirectory(Spool);
            var e = new Dictionary<string, object?>
            {
                ["kind"] = kind, ["at"] = DateTime.UtcNow.ToString("o"), ["name"] = c.Name, ["publisher"] = c.Publisher, ["version"] = c.Version,
                ["sha256"] = c.Sha256, ["path"] = c.Path, ["user"] = user, ["detail"] = detail,
            };
            var tmp = Path.Combine(Spool, "." + Guid.NewGuid().ToString("N"));
            File.WriteAllText(tmp, JsonSerializer.Serialize(e));
            File.Move(tmp, Path.Combine(Spool, Guid.NewGuid().ToString("N") + ".event"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Flush(ServerClient client, CancellationToken ct)
    {
        try
        {
            if (Directory.Exists(Spool))
            {
                var files = Directory.GetFiles(Spool, "*.event").OrderBy(f => File.GetLastWriteTimeUtc(f)).Take(200).ToList();
                var events = new List<Dictionary<string, object?>>();
                foreach (var f in files)
                    try { events.Add(JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(f)) ?? new()); }
                    catch (JsonException) { }
                if (events.Count > 0)
                {
                    client.PostAppControlEventsAsync(events, ct).GetAwaiter().GetResult();
                    foreach (var f in files) try { File.Delete(f); } catch (IOException) { }
                }
            }
            if (Directory.Exists(RequestSpool))
                foreach (var f in Directory.GetFiles(RequestSpool, "*.request").Take(20))
                {
                    var info = new FileInfo(f);
                    try
                    {
                        if (info.Length is > 0 and < 8192 && (info.UnixFileMode & UnixFileMode.OtherWrite) == 0)
                        {
                            var r = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(f)) ?? new();
                            r["user"] = Owner(f);
                            if (!string.IsNullOrWhiteSpace(r.GetValueOrDefault("name")))
                            {
                                client.PostAppControlRequestAsync(r.ToDictionary(kv => kv.Key, kv => (object?)kv.Value), ct).GetAwaiter().GetResult();
                                Log.Info($"Application control: {r["user"]} asked for approval of {r.GetValueOrDefault("name")}.");
                            }
                        }
                    }
                    catch (ServerException ex) { Log.Warn("Application control: request not accepted: " + ex.Message); }
                    catch (JsonException) { }
                    File.Delete(f);
                }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("Application control: could not send events or requests: " + AgentWorker.Describe(ex)); }
    }

    private static string? Owner(string file)
    {
        var r = Shell.Run("stat", ["-c", "%U", file]);
        return r.Ok ? r.Output.Trim() : null;
    }

    /// <summary>zentriq-agent --request &lt;software&gt; [--version v] [--reason text]: any user can ask for approval.</summary>
    public static int Request(string[] args)
    {
        if (args.Length < 2 || args[1].StartsWith("--")) { Console.Error.WriteLine("Usage: zentriq-agent --request <software or package name> [--version <version>] [--reason \"why you need it\"]"); return 2; }
        var name = args[1].Trim();
        if (name.Length > 300) name = name[..300];
        var body = new Dictionary<string, string?> { ["name"] = name, ["version"] = Option(args, "--version"), ["reason"] = Option(args, "--reason") };
        try
        {
            var file = Path.Combine(RequestSpool, Guid.NewGuid().ToString("N"));
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(body));
            File.SetUnixFileMode(file + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(file + ".tmp", file + ".request");
            Console.WriteLine($"Your request for {name} was sent to your administrator. You can install it once it is approved.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("The request could not be saved (" + ex.Message + "). Is the Zentriq Agent installed and running?");
            return 1;
        }
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1].Trim() is { Length: > 0 and <= 1000 } v ? v : null;
        return null;
    }

    // ---- the check (run by the hooks) ----------------------------------------------------------------------

    /// <summary>
    /// Decides on packages about to be installed. Exit code 0: go ahead; 1: refused (block mode), with the message on
    /// standard error for the package manager to show.
    /// </summary>
    public static int Check(IReadOnlyList<SoftwareCandidate> packages, string manager, bool canBlock = true)
    {
        if (packages.Count == 0) return 0;
        var policy = Policy;
        if (policy.Mode == "off") return 0;
        if (Environment.GetEnvironmentVariable("ZENTRIQ_DEPLOY_TOKEN") is { Length: > 10 } token && File.Exists(DeployToken)
            && File.ReadAllText(DeployToken).Trim() == token) return 0;   // installed by the agent itself (Software Deployment)
        var user = Environment.GetEnvironmentVariable("SUDO_USER") ?? Environment.GetEnvironmentVariable("USER") ?? "root";
        var refused = new List<(SoftwareCandidate C, AppDecision D)>();
        foreach (var c in packages)
        {
            var d = AppControl.Evaluate(policy, c);
            if (d.Approved) continue;
            refused.Add((c, d));
            var block = policy.Mode == "block" && canBlock;
            SpoolEvent(block ? "blocked" : "audited", c, user, $"{d.Reason} ({manager})" + (block ? "" : policy.Mode == "block"
                ? " The package manager cannot be stopped from a plugin; the installation went ahead and is removed by the inventory check when automatic removal is on."
                : " Audit mode: the installation was allowed."));
        }
        if (refused.Count == 0 || policy.Mode != "block") return 0;
        if (!canBlock)
        {
            Console.Error.WriteLine($"\nZentriq: {policy.Message}\n  Not approved (reported to your administrator): {string.Join(", ", refused.Select(r => r.C.Name))}");
            return 0;
        }
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Zentriq: " + policy.Message);
        sb.AppendLine("  Not approved: " + string.Join(", ", refused.Select(r => r.C.Name + (r.C.Version is null ? "" : " " + r.C.Version))));
        if (policy.AllowRequests) sb.AppendLine("  To ask for approval: zentriq-agent --request " + refused[0].C.Name + " --reason \"why you need it\"");
        Console.Error.Write(sb.ToString());
        return 1;
    }

    /// <summary>dpkg pre-invoke hook: the .deb files given to the dpkg process that runs the hook (dpkg -i, apt).</summary>
    public static int CheckDpkg()
    {
        // apt passes the archives, or (many packages) a folder of links with --recursive.
        var args = DpkgArguments();
        var debs = args.Where(a => a.EndsWith(".deb", StringComparison.OrdinalIgnoreCase) && File.Exists(a))
            .Concat(args.Where(a => (args.Contains("--recursive") || args.Contains("-R")) && !a.StartsWith('-') && Directory.Exists(a)).SelectMany(d =>
            {
                try { return Directory.EnumerateFiles(d, "*.deb", SearchOption.AllDirectories); } catch (Exception) { return []; }
            }))
            .Distinct().Take(5000).ToList();
        if (debs.Count == 0) return 0;
        var list = new List<SoftwareCandidate>();
        foreach (var deb in debs)
        {
            var f = Shell.Run("dpkg-deb", ["-f", deb, "Package", "Version", "Maintainer", "Section"]);
            if (!f.Ok) continue;
            var fields = f.Output.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
            if (!fields.TryGetValue("Package", out var name)) continue;
            var installed = Shell.Run("dpkg-query", ["-W", "-f=${Status}|${Version}", name]);
            var iv = installed.Ok && installed.Output.StartsWith("install ok installed", StringComparison.Ordinal) ? installed.Output.Split('|').Last() : null;
            list.Add(new SoftwareCandidate
            {
                Name = name, Version = fields.GetValueOrDefault("Version"), Publisher = fields.GetValueOrDefault("Maintainer"), Section = fields.GetValueOrDefault("Section"),
                InstalledVersion = iv, Platform = "linux", Path = deb,
            });
        }
        return Check(list, "dpkg");
    }

    /// <summary>The arguments of the nearest dpkg process above this one.</summary>
    private static List<string> DpkgArguments()
    {
        var pid = Environment.ProcessId;
        for (var i = 0; i < 6; i++)
        {
            var stat = File.Exists($"/proc/{pid}/stat") ? File.ReadAllText($"/proc/{pid}/stat") : "";
            var close = stat.LastIndexOf(')');
            if (close < 0) break;
            var parts = stat[(close + 2)..].Split(' ');
            if (parts.Length < 2 || !int.TryParse(parts[1], out var ppid) || ppid <= 1) break;
            pid = ppid;
            var cmd = File.Exists($"/proc/{pid}/cmdline") ? File.ReadAllText($"/proc/{pid}/cmdline") : "";
            var args = cmd.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (args.Count > 0 && Path.GetFileName(args[0]) == "dpkg") return args.Skip(1).ToList();
        }
        return [];
    }

    /// <summary>dnf and zypper plugins: one package per line, "name \t version \t installed version \t vendor \t group".</summary>
    public static int CheckPackages(TextReader input, string manager)
    {
        var list = new List<SoftwareCandidate>();
        string? line;
        while ((line = input.ReadLine()) is not null && list.Count < 5000)
        {
            var f = line.Split('\t');
            if (f.Length < 1 || string.IsNullOrWhiteSpace(f[0])) continue;
            list.Add(new SoftwareCandidate
            {
                Name = f[0].Trim(), Version = f.Length > 1 && f[1].Length > 0 ? f[1] : null, InstalledVersion = f.Length > 2 && f[2].Length > 0 ? f[2] : null,
                Publisher = f.Length > 3 && f[3].Length > 0 ? f[3] : null, Section = f.Length > 4 && f[4].Length > 0 ? f[4] : null, Platform = "linux",
            });
        }
        return Check(list, manager);
    }

    /// <summary>
    /// zypper commit plugin (libzypp plugin protocol on standard input / output): on COMMITBEGIN the packages to be
    /// installed are checked; refusing answers ERROR, which stops the commit.
    /// </summary>
    public static int ZyppPlugin()
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        while (true)
        {
            var frame = ReadFrame(stdin);
            if (frame is null) return 0;
            var (command, body) = frame.Value;
            if (command == "_DISCONNECT") { WriteFrame(stdout, "ACK", ""); return 0; }
            if (command == "COMMITBEGIN")
            {
                var list = new List<SoftwareCandidate>();
                try
                {
                    using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
                    if (doc.RootElement.TryGetProperty("TransactionStepList", out var steps) && steps.ValueKind == JsonValueKind.Array)
                        foreach (var s in steps.EnumerateArray())
                        {
                            if (!s.TryGetProperty("type", out var t) || t.GetString() != "+") continue;
                            if (!s.TryGetProperty("solvable", out var sol)) continue;
                            var name = sol.TryGetProperty("n", out var n) ? n.GetString() : null;
                            if (string.IsNullOrEmpty(name)) continue;
                            var v = (sol.TryGetProperty("v", out var vv) ? vv.GetString() : null) + (sol.TryGetProperty("r", out var rr) ? "-" + rr.GetString() : "");
                            var q = Shell.Run("rpm", ["-q", "--qf", "%{VERSION}-%{RELEASE}", name]);
                            list.Add(new SoftwareCandidate { Name = name!, Version = v, InstalledVersion = q.Ok ? q.Output.Trim() : null, Platform = "linux" });
                        }
                }
                catch (JsonException) { }
                // libzypp does not let a commit plugin stop the commit: the packages are reported (and removed later by the
                // inventory check when automatic removal is on).
                Check(list, "zypper", canBlock: false);
            }
            WriteFrame(stdout, "ACK", "");
        }
    }

    private static (string Command, string Body)? ReadFrame(Stream s)
    {
        var buf = new MemoryStream();
        int b;
        while ((b = s.ReadByte()) >= 0 && b != 0) buf.WriteByte((byte)b);
        if (b < 0 && buf.Length == 0) return null;
        var text = Encoding.UTF8.GetString(buf.ToArray());
        var split = text.IndexOf("\n\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var body = split < 0 ? "" : text[(split + 2)..];
        return (head.Split('\n')[0].Trim(), body);
    }

    private static void WriteFrame(Stream s, string command, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(command + "\n\n" + body + "\0");
        s.Write(bytes);
        s.Flush();
    }
}

/// <summary>Installs and removes the package-manager hooks.</summary>
internal static class PackageHooks
{
    public const string DpkgHook = "/etc/dpkg/dpkg.cfg.d/zentriq-app-control";
    public const string DnfConf = "/etc/dnf/plugins/zentriq.conf";
    public const string ZyppPlugin = "/usr/lib/zypp/plugins/commit/zentriq";
    private static string Agent => Path.Combine(Paths.Install, "zentriq-agent");

    public static bool Installed => File.Exists(DpkgHook) || DnfPlugin() is { } p && File.Exists(p) || File.Exists(ZyppPlugin);

    public static void Install()
    {
        try
        {
            if (Shell.Exists("dpkg") && Directory.Exists("/etc/dpkg"))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DpkgHook)!);
                Write(DpkgHook, "# Zentriq application control (managed by the Zentriq Agent; removed with it).\n" +
                                $"pre-invoke=\"[ ! -x {Agent} ] || {Agent} --check-dpkg\"\n");
            }
            if (DnfPlugin() is { } plugin)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(plugin)!);
                Write(plugin, DnfPluginSource);
                Directory.CreateDirectory(Path.GetDirectoryName(DnfConf)!);
                Write(DnfConf, "[main]\nenabled=1\n");
            }
            if (Shell.Exists("zypper"))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ZyppPlugin)!);
                Write(ZyppPlugin, $"#!/bin/sh\n# Zentriq application control (managed by the Zentriq Agent).\n[ -x {Agent} ] && exec {Agent} --zypp-plugin\nexit 0\n");
                File.SetUnixFileMode(ZyppPlugin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Application control: could not install the package-manager hooks: " + ex.Message); }
    }

    public static void Remove()
    {
        foreach (var f in new[] { DpkgHook, DnfConf, ZyppPlugin, DnfPlugin() })
            if (f is not null && File.Exists(f))
                try { File.Delete(f); Log.Info("Application control: removed " + f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Could not remove " + f + ": " + ex.Message); }
    }

    private static void Write(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        File.WriteAllText(path, content);
        Log.Info("Application control: package-manager hook " + path);
    }

    private static string? _dnfPlugin;
    private static bool _dnfChecked;

    /// <summary>The dnf (4) plugin file, in the dnf-plugins folder next to the dnf Python package; null without dnf 4.</summary>
    public static string? DnfPlugin()
    {
        if (_dnfChecked) return _dnfPlugin;
        _dnfChecked = true;
        if (!Shell.Exists("dnf")) return null;
        foreach (var py in new[] { "/usr/libexec/platform-python", "/usr/bin/python3" })
        {
            if (!File.Exists(py)) continue;
            var r = Shell.Run(py, ["-c", "import dnf, os; print(os.path.join(os.path.dirname(os.path.dirname(dnf.__file__)), 'dnf-plugins'))"], TimeSpan.FromSeconds(20));
            if (r.Ok && r.Output.Trim().StartsWith('/')) return _dnfPlugin = Path.Combine(r.Output.Trim(), "zentriq.py");
        }
        return null;
    }

    private const string DnfPluginSource = """
        # Zentriq application control (managed by the Zentriq Agent; removed with it).
        # Checks the packages of a transaction against the organisation's approved software before dnf installs them.
        import os
        import subprocess
        import dnf
        import dnf.exceptions

        AGENT = '/opt/zentriq-agent/zentriq-agent'


        class ZentriqAppControl(dnf.Plugin):
            name = 'zentriq'

            def resolved(self):
                if not os.access(AGENT, os.X_OK):
                    return
                tx = self.base.transaction
                if tx is None:
                    return
                installed = self.base.sack.query().installed()
                lines = []
                for p in tx.install_set:
                    old = installed.filter(name=p.name)
                    oldv = old[0].evr if len(old) else ''
                    lines.append('\t'.join([p.name, p.evr or '', oldv, getattr(p, 'vendor', '') or getattr(p, 'packager', '') or '', getattr(p, 'group', '') or '']))
                if not lines:
                    return
                r = subprocess.run([AGENT, '--check-packages', 'dnf'], input='\n'.join(lines) + '\n', stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   universal_newlines=True)
                if r.returncode != 0:
                    raise dnf.exceptions.Error((r.stderr or r.stdout or 'Not approved by Zentriq application control.').strip())

        """;
}
