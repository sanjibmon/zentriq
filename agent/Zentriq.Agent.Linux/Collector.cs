using System.Collections.Concurrent;
using Zentriq.Inventory;

namespace Zentriq.AgentLinux;

/// <summary>
/// Collects this device's inventory with the same read-only commands Zentriq uses over SSH (UnixCollector), run
/// locally as root, so serial numbers, firmware, disk encryption, firewall and SSH settings are complete.
/// </summary>
internal static class LinuxCollector
{
    private static string? Nice => File.Exists("/usr/bin/nice") ? "/usr/bin/nice" : File.Exists("/bin/nice") ? "/bin/nice" : null;

    public static InventoryDocument Collect()
    {
        var commands = UnixCollector.LinuxCommands.Concat(UnixCollector.LinuxExtraCommands).ToList();
        var outputs = new ConcurrentDictionary<string, string>();
        var errors = new ConcurrentBag<string>();
        // A few at a time, at low priority: the slow ones (package lists, pending updates) do not hold up the rest.
        Parallel.ForEach(commands, new ParallelOptions { MaxDegreeOfParallelism = 4 }, c =>
        {
            var timeout = TimeSpan.FromSeconds(c.Key is "updates" or "packages" ? 120 : 45);
            var r = Nice is { } nice ? Shell.Run(nice, ["-n", "10", "/bin/sh", "-c", c.Value], timeout) : Shell.Run("/bin/sh", ["-c", c.Value], timeout);
            if (r.Code == -1) errors.Add($"{c.Key}: {r.Output}");
            outputs[c.Key] = r.Code < 0 ? "" : r.Output;
        });
        var uname = Shell.Run("uname", ["-s", "-m", "-r"]).Output.Replace(' ', '\n');
        // uname -s -m -r prints "Linux 6.8.0 x86_64" in kernel order (s, r, m): put it in the order ParseLinux expects.
        var u = uname.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var probe = u.Length >= 3 ? $"{u[0]}\n{u[2]}\n{u[1]}\n" : uname;
        var doc = UnixCollector.ParseLinux(outputs, probe, AgentVersion.Current);
        doc.Source = "agent";
        if (doc.Device.Hostname is null)
            try { var h = File.ReadAllText("/proc/sys/kernel/hostname").Trim(); if (h.Length > 0) doc.Device.Hostname = h.Split('.')[0]; } catch (IOException) { }
        doc.Errors.RemoveAll(e => e.StartsWith("serial/uuid", StringComparison.Ordinal) && Shell.IsRoot);
        doc.Errors.AddRange(errors);
        return doc;
    }
}
