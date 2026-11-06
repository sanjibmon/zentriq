using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zentriq.Inventory;

namespace Zentriq.AgentLinux;

/// <summary>
/// Patch management on Linux with the distribution's package manager: scan (apt: <c>apt-get update</c> and
/// <c>apt list --upgradable</c>, security updates from the -security suites; dnf / yum: <c>check-update</c> and
/// <c>updateinfo</c> for security advisories and their severity; zypper: <c>list-updates</c>), install the chosen
/// packages (only upgrades of installed packages), scan again and report both; restart when the system asks for it
/// (/var/run/reboot-required, needs-restarting) unless the deployment says not to.
/// </summary>
internal static class Patches
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromHours(2);

    private static Dictionary<string, string> Env()
    {
        // The package managers the agent runs itself are not checked by application control.
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        Directory.CreateDirectory(AppControlAgent.Folder);
        Paths.WriteSecret(AppControlAgent.DeployToken, Encoding.UTF8.GetBytes(token));
        return new Dictionary<string, string> { ["ZENTRIQ_DEPLOY_TOKEN"] = token };
    }

    private static void EndEnv() { try { File.Delete(AppControlAgent.DeployToken); } catch (IOException) { } }

    public static (bool Ok, string? State, string Message) Run(ServerClient client, AgentAction a, CancellationToken ct)
    {
        if (!Shell.IsRoot) return (false, null, "The agent does not run as root; it cannot manage packages.");
        if (a.Operation == "scan")
        {
            var report = Scan(refresh: true);
            Post(client, report, ct);
            return report["ok"] is true ? (true, "scanned", Count(report) == 0 ? "No missing updates." : $"{Count(report)} missing update(s) found.") : (false, null, (string)report["message"]!);
        }
        if (a.Operation != "install") return (false, null, "Unknown patch operation " + a.Operation + ".");

        Dictionary<string, JsonElement> p;
        try { p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.Argument ?? "{}") ?? new(); }
        catch (JsonException) { return (false, null, "The patch request is not valid."); }
        var packages = p.TryGetValue("packages", out var pk) && pk.ValueKind == JsonValueKind.Array
            ? pk.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(Safe).Distinct().Take(500).ToList() : [];
        var ids = p.TryGetValue("ids", out var iv) && iv.ValueKind == JsonValueKind.Array
            ? iv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
        if (packages.Count == 0) return (false, null, "No packages were chosen.");
        var reboot = p.TryGetValue("reboot", out var rb) && rb.ValueKind == JsonValueKind.String ? rb.GetString() : "when-needed";
        var delay = p.TryGetValue("rebootDelayMinutes", out var dv) && dv.ValueKind == JsonValueKind.Number ? Math.Clamp(dv.GetInt32(), 1, 240) : 10;
        var message = p.TryGetValue("rebootMessage", out var mv) && mv.ValueKind == JsonValueKind.String ? mv.GetString() : null;

        Log.Info($"Patch management: upgrading {packages.Count} package(s): {string.Join(" ", packages.Take(20))}{(packages.Count > 20 ? " …" : "")}.");
        Shell.Result r;
        var env = Env();
        try
        {
            r = Shell.Exists("apt-get")
                ? Shell.Run("apt-get", ["install", "-y", "-q", "--only-upgrade", "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold", .. packages], InstallTimeout, env)
                : Shell.Exists("dnf") ? Shell.Run("dnf", ["upgrade", "-y", "-q", .. packages], InstallTimeout, env)
                : Shell.Exists("yum") ? Shell.Run("yum", ["update", "-y", "-q", .. packages], InstallTimeout, env)
                : Shell.Exists("zypper") ? Shell.Run("zypper", ["--non-interactive", "--quiet", "update", .. packages], InstallTimeout, env)
                : new Shell.Result(-2, "", "No supported package manager (apt, dnf, yum, zypper) was found.");
        }
        finally { EndEnv(); }
        var after = Scan(refresh: false);
        var still = after["ok"] is true ? ((List<Dictionary<string, object?>>)after["updates"]!).Select(u => (string)u["package"]!).ToHashSet(StringComparer.Ordinal) : null;
        var err = r.Ok ? null : ServiceActions.Last(r.Text);
        var results = new List<Dictionary<string, object?>>();
        var byPackage = ids.Where(i => i.StartsWith("linux:", StringComparison.Ordinal)).Select(i => (Id: i, Package: i.Split(':')[1])).ToList();
        var rebootNeeded = RebootPending();
        int ok = 0, failed = 0;
        foreach (var (id, pkg) in byPackage)
        {
            var good = still is not null ? !still.Contains(pkg) : r.Ok;
            if (good) ok++; else failed++;
            results.Add(new() { ["id"] = id, ["ok"] = good, ["reboot"] = good && rebootNeeded, ["message"] = good ? "Upgraded." : err ?? "The package was not upgraded." });
        }
        after["results"] = results;
        if (after["ok"] is not true) after["rebootPending"] = rebootNeeded;
        Post(client, after, ct);
        if (r.Code == -1) return (false, null, "The package manager did not finish within 2 hours.");
        var text = $"{ok} of {ok + failed} package(s) upgraded" + (failed > 0 ? $"; {failed} not upgraded ({err ?? "see the agent log"})" : "") + ".";
        if (rebootNeeded && ok > 0)
        {
            if (reboot == "never") text += " The system asks for a restart (not restarted: the deployment says not to).";
            else
            {
                var notice = (string.IsNullOrWhiteSpace(message) ? "Updates were installed. The system restarts in a few minutes." : message!).Replace('"', '\'');
                Shell.Run("shutdown", ["-r", $"+{delay}", notice.Length > 300 ? notice[..300] : notice], TimeSpan.FromSeconds(30));
                text += $" The system restarts in {delay} minute(s).";
            }
        }
        AgentService.RequestInventory();
        return (failed == 0, failed == 0 ? "installed" : null, text);
    }

    private static int Count(Dictionary<string, object?> report) => report["updates"] is List<Dictionary<string, object?>> l ? l.Count : 0;

    private static bool Safe(string name) => name.Length is > 0 and <= 200 && (char.IsAsciiLetterOrDigit(name[0])) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-');

    private static void Post(ServerClient client, Dictionary<string, object?> report, CancellationToken ct)
    {
        try { client.PostPatchesAsync(report, ct).GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warn("Could not send the patch scan: " + ex.Message); }
    }

    public static bool RebootPending()
    {
        if (File.Exists("/var/run/reboot-required") || File.Exists("/run/reboot-required") || File.Exists("/run/reboot-needed")) return true;
        if (Shell.Exists("needs-restarting")) return Shell.Run("needs-restarting", ["-r"], TimeSpan.FromSeconds(60)).Code == 1;
        return false;
    }

    private static Dictionary<string, object?> Fail(string message) => new() { ["ok"] = false, ["message"] = message };

    public static Dictionary<string, object?> Scan(bool refresh)
    {
        List<LinuxUpdate> updates;
        string source;
        if (Shell.Exists("apt-get"))
        {
            source = "apt";
            if (refresh)
            {
                var up = Shell.Run("apt-get", ["update", "-q"], ScanTimeout);
                if (!up.Ok) return Fail("apt-get update failed: " + ServiceActions.Last(up.Text));
            }
            var r = Shell.Run("apt", ["list", "--upgradable"], ScanTimeout);
            if (!r.Ok) return Fail("apt list --upgradable failed: " + ServiceActions.Last(r.Text));
            updates = LinuxPatches.ParseApt(r.Output);
        }
        else if (Shell.Exists("dnf") || Shell.Exists("yum"))
        {
            var pm = Shell.Exists("dnf") ? "dnf" : "yum";
            source = pm;
            var r = Shell.Run(pm, refresh ? new[] { "-q", "check-update" } : new[] { "-q", "-C", "check-update" }, ScanTimeout);
            if (r.Code is not (0 or 100)) return Fail($"{pm} check-update failed: " + ServiceActions.Last(r.Text));
            updates = LinuxPatches.ParseCheckUpdate(r.Output);
            if (updates.Count > 0)
            {
                var info = Shell.Run(pm, pm == "dnf" ? new[] { "-q", "-C", "updateinfo", "list", "--updates" } : new[] { "-q", "-C", "updateinfo", "list", "updates" }, ScanTimeout);
                if (info.Ok) LinuxPatches.Apply(updates, LinuxPatches.ParseUpdateInfo(info.Output));
            }
        }
        else if (Shell.Exists("zypper"))
        {
            source = "zypper";
            var r = Shell.Run("zypper", refresh ? new[] { "--non-interactive", "--quiet", "list-updates" } : new[] { "--non-interactive", "--quiet", "--no-refresh", "list-updates" }, ScanTimeout);
            if (!r.Ok) return Fail("zypper list-updates failed: " + ServiceActions.Last(r.Text));
            updates = LinuxPatches.ParseZypper(r.Output);
        }
        else return Fail("No supported package manager (apt, dnf, yum, zypper) was found.");

        var list = updates.GroupBy(u => u.Package, StringComparer.Ordinal).Select(g => g.First()).Select(u => new Dictionary<string, object?>
        {
            ["package"] = u.Package, ["version"] = u.Version, ["title"] = $"{u.Package} {u.Version}", ["product"] = u.Repository,
            ["classification"] = u.Security ? "security" : "updates", ["severity"] = u.Severity, ["kb"] = null, ["url"] = null,
            ["reboot"] = u.Package.StartsWith("kernel", StringComparison.Ordinal) || u.Package.StartsWith("linux-image", StringComparison.Ordinal) || u.Package is "glibc" or "libc6" or "systemd",
        }).ToList();
        return new Dictionary<string, object?> { ["ok"] = true, ["source"] = source, ["rebootPending"] = RebootPending(), ["updates"] = list };
    }
}

/// <summary>
/// Scripts from Scripts &amp; Remediation on Linux (Bash, sh, Python 3): checked against the SHA-256 from the server,
/// written to a folder only root can read, run as root with the parameters and a time limit (the process group is
/// ended when it is reached); at most 64 KB of standard output and error and the exit code are sent back. A
/// remediation runs the detection first and the fix only when the detection exits with a code other than 0.
/// </summary>
internal static class Scripts
{
    private static string Root => Path.Combine(Paths.Data, "scripts");
    private const int MaxOutput = 64 * 1024;

    private sealed record Spec(string Name, string Language, string Content, string Sha256, int Timeout);
    private sealed record Outcome(int ExitCode, string Stdout, string Stderr, bool TimedOut, long Ms);

    public static (bool Ok, string? State, string Message) Run(ServerClient client, AgentAction a, CancellationToken ct)
    {
        Dictionary<string, JsonElement> p;
        try { p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.Argument ?? "{}") ?? new(); }
        catch (JsonException) { return (false, null, "The script request is not valid."); }
        var fix = Read(p);
        if (fix is null) return (false, null, "The script request is not valid.");
        var parameters = p.TryGetValue("parameters", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
        Spec? detect = null;
        if (p.TryGetValue("detect", out var dv) && dv.ValueKind == JsonValueKind.Object)
        {
            detect = Read(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dv.GetRawText()) ?? new());
            if (detect is null) return (false, null, "The detection script in the request is not valid.");
        }
        foreach (var s in detect is null ? new[] { fix } : new[] { detect, fix })
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s.Content))), s.Sha256, StringComparison.OrdinalIgnoreCase))
                return (false, null, $"The script {s.Name} does not match its checksum from the server; it was not run.");

        var o = new StringBuilder();
        var e = new StringBuilder();
        var total = Stopwatch.StartNew();
        Outcome last;
        bool ok; string outcome, message;
        if (detect is null)
        {
            last = Execute(fix, parameters, ct);
            o.Append(last.Stdout); e.Append(last.Stderr);
            ok = !last.TimedOut && last.ExitCode == 0;
            outcome = last.TimedOut ? "timeout" : ok ? "ok" : "failed";
            message = last.TimedOut ? $"{fix.Name} did not finish within {fix.Timeout} seconds and was stopped." : $"{fix.Name} ended with exit code {last.ExitCode} after {(last.Ms < 1000 ? $"{last.Ms} ms" : $"{last.Ms / 1000.0:0.0} s")}.";
        }
        else
        {
            var before = Execute(detect, null, ct);
            Section(o, e, "Detection: " + detect.Name, before);
            if (!before.TimedOut && before.ExitCode == 0) { last = before; ok = true; outcome = "compliant"; message = $"Compliant: {detect.Name} found nothing to fix."; }
            else if (before.TimedOut) { last = before; ok = false; outcome = "timeout"; message = $"The detection {detect.Name} did not finish within {detect.Timeout} seconds; the fix was not run."; }
            else
            {
                last = Execute(fix, parameters, ct);
                Section(o, e, "Fix: " + fix.Name, last);
                var after = last.TimedOut ? null : Execute(detect, null, ct);
                if (after is not null) Section(o, e, "Check after the fix: " + detect.Name, after);
                ok = after is { TimedOut: false, ExitCode: 0 };
                outcome = ok ? "remediated" : last.TimedOut ? "timeout" : "failed";
                message = ok ? $"Remediated: {fix.Name} fixed what {detect.Name} found."
                    : last.TimedOut ? $"The fix {fix.Name} did not finish within {fix.Timeout} seconds and was stopped."
                    : last.ExitCode != 0 ? $"The fix {fix.Name} ended with exit code {last.ExitCode}."
                    : $"The fix {fix.Name} ran, but {detect.Name} still reports a problem (exit code {after?.ExitCode}).";
            }
        }
        try
        {
            client.PostOutputAsync(a.Id, new Dictionary<string, object?>
            {
                ["exitCode"] = last.TimedOut ? null : last.ExitCode, ["stdout"] = Clip(o.ToString()), ["stderr"] = Clip(e.ToString()), ["outcome"] = outcome,
                ["durationMs"] = (int)Math.Min(int.MaxValue, total.ElapsedMilliseconds),
            }, ct).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Log.Warn("Could not send the script output: " + ex.Message); }
        return (ok, outcome, message);
    }

    private static void Section(StringBuilder o, StringBuilder e, string title, Outcome r)
    {
        o.Append("--- ").Append(title).Append(" (exit code ").Append(r.TimedOut ? "timeout" : r.ExitCode.ToString()).Append(") ---\n").Append(r.Stdout);
        if (!r.Stdout.EndsWith('\n')) o.Append('\n');
        if (r.Stderr.Length > 0) e.Append("--- ").Append(title).Append(" ---\n").Append(r.Stderr).Append('\n');
    }

    private static string Clip(string s) => s.Length > MaxOutput ? s[..MaxOutput] + "\n… (output shortened)" : s;

    private static Spec? Read(Dictionary<string, JsonElement> p)
    {
        string? S(string k) => p.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var timeout = p.TryGetValue("timeout", out var t) && t.ValueKind == JsonValueKind.Number ? Math.Clamp(t.GetInt32(), 10, 14400) : 600;
        var s = new Spec(S("name") ?? "script", S("language") ?? "", S("content") ?? "", S("sha256") ?? "", timeout);
        return s.Content.Length == 0 || s.Sha256.Length != 64 || s.Language is not ("bash" or "sh" or "python") ? null : s;
    }

    /// <summary>Splits the parameters like a shell would (quotes and backslashes; no expansion).</summary>
    public static List<string> SplitArgs(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        var cur = new StringBuilder();
        var inArg = false; char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\' && quote == '"' && i + 1 < text.Length && text[i + 1] is '"' or '\\') cur.Append(text[++i]);
                else cur.Append(c);
            }
            else if (c is '"' or '\'') { quote = c; inArg = true; }
            else if (c == '\\' && i + 1 < text.Length) { cur.Append(text[++i]); inArg = true; }
            else if (char.IsWhiteSpace(c)) { if (inArg) { list.Add(cur.ToString()); cur.Clear(); inArg = false; } }
            else { cur.Append(c); inArg = true; }
        }
        if (inArg) list.Add(cur.ToString());
        return list;
    }

    private static Outcome Execute(Spec s, string? parameters, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        try { File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch (Exception) { }
        var file = Path.Combine(Root, Guid.NewGuid().ToString("N") + (s.Language == "python" ? ".py" : ".sh"));
        File.WriteAllText(file, s.Content, new UTF8Encoding(false));
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            var exe = s.Language switch { "bash" => "/bin/bash", "sh" => "/bin/sh", _ => Shell.Exists("python3") ? "python3" : "python" };
            if (s.Language == "bash" && !File.Exists(exe)) return new Outcome(127, "", "bash is not installed on this device.", false, 0);
            if (s.Language == "python" && !Shell.Exists(exe)) return new Outcome(127, "", "Python 3 is not installed on this device.", false, 0);
            Log.Info($"Script: running {s.Name} ({s.Language}, time limit {s.Timeout} s).");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = Root };
            psi.ArgumentList.Add(file);
            foreach (var arg in SplitArgs(parameters)) psi.ArgumentList.Add(arg);
            psi.Environment["LC_ALL"] = "C.UTF-8";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
            var o = new StringBuilder();
            var e = new StringBuilder();
            var sw = Stopwatch.StartNew();
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, d) => { if (d.Data is null) return; lock (o) if (o.Length < MaxOutput + 1024) o.Append(d.Data).Append('\n'); };
            proc.ErrorDataReceived += (_, d) => { if (d.Data is null) return; lock (e) if (e.Length < MaxOutput + 1024) e.Append(d.Data).Append('\n'); };
            proc.Start();
            proc.StandardInput.Close();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            var deadline = DateTime.UtcNow.AddSeconds(s.Timeout);
            var timedOut = false;
            while (!proc.WaitForExit(1000))
                if (DateTime.UtcNow > deadline || ct.IsCancellationRequested) { timedOut = true; try { proc.Kill(entireProcessTree: true); } catch (Exception) { } break; }
            if (!timedOut) proc.WaitForExit(); else proc.WaitForExit(10000);
            string so, se;
            lock (o) so = o.ToString();
            lock (e) se = e.ToString();
            return new Outcome(timedOut ? -1 : proc.ExitCode, so, se, timedOut, sw.ElapsedMilliseconds);
        }
        catch (System.ComponentModel.Win32Exception ex) { return new Outcome(127, "", ex.Message, false, 0); }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }
}
