using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Zentriq.AgentLinux;

/// <summary>Where the agent keeps its files. Everything under /var/lib/zentriq-agent is root-only (0700).</summary>
internal static class Paths
{
    public const string ServiceName = "zentriq-agent";
    public static string Install => Environment.GetEnvironmentVariable("ZENTRIQ_AGENT_HOME") ?? "/opt/zentriq-agent";
    public static string Data => Environment.GetEnvironmentVariable("ZENTRIQ_AGENT_DATA") ?? "/var/lib/zentriq-agent";
    public static string Logs => Environment.GetEnvironmentVariable("ZENTRIQ_AGENT_LOGS") ?? "/var/log/zentriq-agent";
    public static string StateFile => Path.Combine(Data, "state.json");
    public static string Certificate => Path.Combine(Data, "agent.pfx");
    public static string Protection => Path.Combine(Data, "protection.json");
    public static string Events => Path.Combine(Data, "events.json");
    public static string Queue => Path.Combine(Data, "queue");
    public static string QueuedInventory => Path.Combine(Queue, "inventory.json.gz");
    public static string Updates => Path.Combine(Data, "updates");
    public static string RemoteSupport => Path.Combine(Data, "remote-support");
    /// <summary>How the agent was installed: deb | rpm | tar (written by the package).</summary>
    public static string InstalledBy => Path.Combine(Install, "installed-by");
    /// <summary>systemd drop-in that refuses "systemctl stop" while tamper protection is on.</summary>
    public const string StopDropIn = "/etc/systemd/system/zentriq-agent.service.d/10-protection.conf";
    /// <summary>One-time permission to remove the package, written by --uninstall with a valid code.</summary>
    public static string UninstallAllowed => Path.Combine(Data, "uninstall-allowed");

    public static void EnsureData()
    {
        foreach (var d in new[] { Data, Queue, Updates })
        {
            Directory.CreateDirectory(d);
            try { File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch (Exception) { }
        }
    }

    /// <summary>Writes a file readable by root only (atomically).</summary>
    public static void WriteSecret(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            fs.Write(content);
        try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (Exception) { }
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Plain-text log in /var/log/zentriq-agent (rotated at 5 MB, 5 files) and the journal (stdout). Never logs secrets.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    public static bool Console { get; set; }
    /// <summary>Running under systemd: stdout goes to the journal.</summary>
    public static bool Journal { get; set; }
    private static string File => Path.Combine(Paths.Logs, "agent.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : message + ": " + ex.GetType().Name + ": " + ex.Message);

    private static void Write(string level, string message)
    {
        var line = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'") + " " + level + " " + message;
        if (Console) System.Console.WriteLine(line);
        else if (Journal) System.Console.WriteLine((level.StartsWith("ERR") ? "<3>" : level.StartsWith("WARN") ? "<4>" : "<6>") + message);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.Logs);
                var fi = new FileInfo(File);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    for (var i = 4; i >= 1; i--)
                    {
                        var src = File + "." + i;
                        if (System.IO.File.Exists(src)) System.IO.File.Move(src, File + "." + (i + 1), overwrite: true);
                    }
                    System.IO.File.Move(File, File + ".1", overwrite: true);
                }
                System.IO.File.AppendAllText(File, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal static class AgentVersion
{
    public static string Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is null) return "1.0.0";
            return v.Revision > 0 ? v.ToString(4) : v.ToString(3);
        }
    }
}

/// <summary>Runs a program and returns its exit code and output (stdout + stderr), with a time limit.</summary>
internal static class Shell
{
    /// <summary>Output: standard output; Error: standard error; Text: both (for messages).</summary>
    public sealed record Result(int Code, string Output, string Error = "")
    {
        public bool Ok => Code == 0;
        public string Text => (Output + (Output.Length > 0 && Error.Length > 0 ? "\n" : "") + Error).Trim();
    }

    public static Result Run(string file, IEnumerable<string> args, TimeSpan? timeout = null, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";
        psi.Environment["LANG"] = "C";
        psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(2)).TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return new Result(-1, $"{Path.GetFileName(file)} did not finish within {(timeout ?? TimeSpan.FromMinutes(2)).TotalSeconds:0} seconds.");
            }
            p.WaitForExit();
            return new Result(p.ExitCode, stdout.Result.Trim(), stderr.Result.Trim());
        }
        catch (System.ComponentModel.Win32Exception ex) { return new Result(-2, $"{file}: {ex.Message}"); }
    }

    /// <summary>Runs a shell command line (/bin/sh -c).</summary>
    public static Result Sh(string command, TimeSpan? timeout = null) => Run("/bin/sh", ["-c", command], timeout);

    public static bool Exists(string program) =>
        program.StartsWith('/') ? File.Exists(program) : (Environment.GetEnvironmentVariable("PATH") ?? "/usr/sbin:/usr/bin:/sbin:/bin")
            .Split(':').Concat(["/usr/sbin", "/sbin", "/usr/bin", "/bin"]).Any(d => File.Exists(Path.Combine(d, program)));

    public static bool IsRoot => Environment.UserName == "root" || geteuid() == 0;

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    public static bool Systemd => Directory.Exists("/run/systemd/system");
}
