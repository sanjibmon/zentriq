using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Zentriq.Agent;

/// <summary>
/// Scripts from Scripts &amp; Remediation: the script (PowerShell, Command Prompt or Python) is checked against the SHA-256
/// the server sent, written to a folder only SYSTEM and Administrators can change, run as SYSTEM with its parameters
/// and a time limit (the process tree is ended when it is reached), and its output (at most 64 KB of standard output
/// and of standard error) and exit code are sent back. A remediation runs the detection script first: exit code 0
/// means the device is fine; otherwise the fix runs and the detection runs again to confirm.
/// </summary>
internal static class ScriptRunner
{
    private static string Root => Path.Combine(AgentPaths.Root, "Scripts");
    private const int MaxOutput = 64 * 1024;

    internal sealed class Result
    {
        public int ExitCode { get; set; } = -1;
        public string Stdout { get; set; } = "";
        public string Stderr { get; set; } = "";
        public bool TimedOut { get; set; }
        public long Milliseconds { get; set; }
    }

    private sealed class Spec
    {
        public string Name = "script", Language = "", Content = "", Sha256 = "";
        public int Timeout = 600;
    }

    public static (bool Ok, string? State, string Message) Run(ServerClient client, AgentAction a, CancellationToken ct)
    {
        Dictionary<string, JsonElement> p;
        try { p = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.Argument ?? "{}") ?? new Dictionary<string, JsonElement>(); }
        catch (JsonException) { return (false, null, "The script request is not valid."); }
        var fix = Read(p);
        if (fix is null) return (false, null, "The script request is not valid.");
        var parameters = p.TryGetValue("parameters", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
        if (parameters is not null && (parameters.IndexOf('\n') >= 0 || parameters.IndexOf('\r') >= 0)) return (false, null, "Parameters must be on one line.");
        Spec? detect = null;
        if (p.TryGetValue("detect", out var dv) && dv.ValueKind == JsonValueKind.Object)
        {
            detect = Read(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dv.GetRawText()) ?? new Dictionary<string, JsonElement>());
            if (detect is null) return (false, null, "The detection script in the request is not valid.");
        }
        foreach (var s in detect is null ? new[] { fix } : new[] { detect, fix })
            if (!string.Equals(Sha(s.Content), s.Sha256, StringComparison.OrdinalIgnoreCase))
                return (false, null, $"The script {s.Name} does not match its checksum from the server; it was not run.");

        bool ok; string message, outcome;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        Result last;
        var total = Stopwatch.StartNew();
        if (detect is null)
        {
            last = Execute(fix, parameters, ct);
            stdout.Append(last.Stdout); stderr.Append(last.Stderr);
            ok = !last.TimedOut && last.ExitCode == 0;
            outcome = last.TimedOut ? "timeout" : ok ? "ok" : "failed";
            message = last.TimedOut ? $"{fix.Name} did not finish within {fix.Timeout} seconds and was stopped." : $"{fix.Name} ended with exit code {last.ExitCode} after {Seconds(last.Milliseconds)}.";
        }
        else
        {
            var before = Execute(detect, null, ct);
            Section(stdout, stderr, "Detection: " + detect.Name, before);
            if (!before.TimedOut && before.ExitCode == 0)
            {
                last = before;
                ok = true; outcome = "compliant";
                message = $"Compliant: {detect.Name} found nothing to fix.";
            }
            else if (before.TimedOut)
            {
                last = before;
                ok = false; outcome = "timeout";
                message = $"The detection {detect.Name} did not finish within {detect.Timeout} seconds; the fix was not run.";
            }
            else
            {
                last = Execute(fix, parameters, ct);
                Section(stdout, stderr, "Fix: " + fix.Name, last);
                var after = last.TimedOut ? null : Execute(detect, null, ct);
                if (after is not null) Section(stdout, stderr, "Check after the fix: " + detect.Name, after);
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
                ["exitCode"] = last.TimedOut ? (int?)null : last.ExitCode, ["stdout"] = Clip(stdout.ToString()), ["stderr"] = Clip(stderr.ToString()),
                ["outcome"] = outcome, ["durationMs"] = (int)Math.Min(int.MaxValue, total.ElapsedMilliseconds),
            }, ct).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Log.Warn("Could not send the script output: " + ex.Message); }
        return (ok, outcome, message);
    }

    private static void Section(StringBuilder o, StringBuilder e, string title, Result r)
    {
        o.Append("--- ").Append(title).Append(" (exit code ").Append(r.TimedOut ? "timeout" : r.ExitCode.ToString(CultureInfo.InvariantCulture)).Append(") ---\r\n").Append(r.Stdout);
        if (!r.Stdout.EndsWith("\n", StringComparison.Ordinal)) o.Append("\r\n");
        if (r.Stderr.Length > 0) e.Append("--- ").Append(title).Append(" ---\r\n").Append(r.Stderr).Append("\r\n");
    }

    private static string Seconds(long ms) => ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0} s";

    private static string Clip(string s) => s.Length > MaxOutput ? s.Substring(0, MaxOutput) + "\r\n… (output shortened)" : s;

    private static Spec? Read(Dictionary<string, JsonElement> p)
    {
        string? S(string k) => p.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var s = new Spec { Name = S("name") ?? "script", Language = S("language") ?? "", Content = S("content") ?? "", Sha256 = S("sha256") ?? "" };
        if (p.TryGetValue("timeout", out var t) && t.ValueKind == JsonValueKind.Number) s.Timeout = Math.Max(10, Math.Min(14400, t.GetInt32()));
        return s.Content.Length == 0 || s.Sha256.Length != 64 || s.Language is not ("powershell" or "cmd" or "python") ? null : s;
    }

    private static string Sha(string content)
    {
        using var h = SHA256.Create();
        return BitConverter.ToString(h.ComputeHash(new UTF8Encoding(false).GetBytes(content))).Replace("-", "").ToLowerInvariant();
    }

    private static Result Execute(Spec s, string? parameters, CancellationToken ct)
    {
        AgentUpdater.PrepareFolder(Root);
        var ext = s.Language switch { "powershell" => ".ps1", "cmd" => ".cmd", _ => ".py" };
        var file = Path.Combine(Root, Guid.NewGuid().ToString("N") + ext);
        // PowerShell 5.1 reads UTF-8 with a byte order mark; cmd.exe must not see one.
        File.WriteAllText(file, s.Content, new UTF8Encoding(s.Language == "powershell"));
        try
        {
            string exe, args;
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            Encoding enc = oem;
            switch (s.Language)
            {
                case "powershell":
                    exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                    args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\" {parameters}";
                    break;
                case "cmd":
                    exe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                    args = $"/d /c \"\"{file}\" {parameters}\"";
                    break;
                default:
                    var py = FindPython();
                    if (py is null) return new Result { ExitCode = 9009, Stderr = "Python is not installed on this device (python.exe or py.exe was not found)." };
                    exe = py;
                    args = $"\"{file}\" {parameters}";
                    enc = Encoding.UTF8;
                    break;
            }
            Log.Info($"Script: running {s.Name} ({s.Language}, time limit {s.Timeout} s).");
            return Start(exe, args.TrimEnd(), Root, TimeSpan.FromSeconds(s.Timeout), enc, ct, s.Language == "python" ? new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8" } : null);
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    private static string? FindPython()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "").Split(';'))
        {
            if (dir.Trim().Length == 0 || dir.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            foreach (var name in new[] { "python.exe", "py.exe" })
                try { var f = Path.Combine(dir.Trim(), name); if (File.Exists(f)) return f; } catch (ArgumentException) { }
        }
        var launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe");
        return File.Exists(launcher) ? launcher : null;
    }

    /// <summary>Runs a program with redirected output (bounded), a time limit (the whole process tree is ended) and cancellation.</summary>
    internal static Result Start(string exe, string args, string workDir, TimeSpan timeout, Encoding encoding, CancellationToken ct, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = encoding, StandardErrorEncoding = encoding, WorkingDirectory = workDir,
        };
        if (env is not null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
        var o = new StringBuilder();
        var e = new StringBuilder();
        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, d) => { if (d.Data is null) return; lock (o) if (o.Length < MaxOutput + 1024) o.Append(d.Data).Append("\r\n"); };
        proc.ErrorDataReceived += (_, d) => { if (d.Data is null) return; lock (e) if (e.Length < MaxOutput + 1024) e.Append(d.Data).Append("\r\n"); };
        try { proc.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { return new Result { ExitCode = 9009, Stderr = $"{Path.GetFileName(exe)} could not be started: {ex.Message}" }; }
        try { proc.StandardInput.Close(); } catch (IOException) { }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        var deadline = DateTime.UtcNow + timeout;
        var timedOut = false;
        while (!proc.WaitForExit(1000))
        {
            if (DateTime.UtcNow > deadline || ct.IsCancellationRequested) { timedOut = true; KillTree(proc.Id); break; }
        }
        if (!timedOut) proc.WaitForExit();   // drains the output
        else proc.WaitForExit(10000);
        string so, se;
        lock (o) so = o.ToString();
        lock (e) se = e.ToString();
        return new Result { ExitCode = timedOut ? -1 : proc.ExitCode, Stdout = so, Stderr = se, TimedOut = timedOut, Milliseconds = sw.ElapsedMilliseconds };
    }

    private static void KillTree(int pid)
    {
        try
        {
            using var k = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), $"/T /F /PID {pid}") { UseShellExecute = false, CreateNoWindow = true });
            k?.WaitForExit(15000);
        }
        catch (Exception ex) { Log.Warn("Could not stop the script: " + ex.Message); }
    }
}
