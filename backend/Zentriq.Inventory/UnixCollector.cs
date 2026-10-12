using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Zentriq.Inventory;

/// <summary>
/// Linux and macOS inventory over SSH. The collector runs a fixed list of read-only commands (no sudo,
/// no writes); this class holds the commands and turns their output into an <see cref="InventoryDocument"/>.
/// Parsing is pure so it is unit-tested without a remote host.
/// </summary>
public static class UnixCollector
{
    /// <summary>First command: identifies the platform.</summary>
    public const string ProbeCommand = "uname -s; uname -m; uname -r";

    public static readonly IReadOnlyDictionary<string, string> LinuxCommands = new Dictionary<string, string>
    {
        ["hostname"] = "hostname -f 2>/dev/null || hostname",
        ["osrelease"] = "cat /etc/os-release 2>/dev/null",
        ["cpuinfo"] = "cat /proc/cpuinfo 2>/dev/null",
        ["meminfo"] = "cat /proc/meminfo 2>/dev/null",
        ["dmi"] = "for f in sys_vendor product_name product_serial product_uuid chassis_type bios_vendor bios_version bios_date; do printf '%s=' $f; cat /sys/class/dmi/id/$f 2>/dev/null || echo; done",
        ["df"] = "df -P -k -T -x tmpfs -x devtmpfs -x squashfs -x overlay 2>/dev/null || df -P -k 2>/dev/null",
        ["ipaddr"] = "ip -o addr show 2>/dev/null",
        ["iplink"] = "ip -o link show 2>/dev/null",
        ["route"] = "ip route show default 2>/dev/null",
        ["packages"] = "if command -v dpkg-query >/dev/null 2>&1; then dpkg-query -W -f='dpkg\\t${Package}\\t${Version}\\t${Architecture}\\n' 2>/dev/null; fi; if command -v rpm >/dev/null 2>&1; then rpm -qa --qf 'rpm\\t%{NAME}\\t%{VERSION}-%{RELEASE}\\t%{ARCH}\\t%{VENDOR}\\n' 2>/dev/null; fi",
        ["uptime"] = "cat /proc/uptime 2>/dev/null",
        ["virt"] = "systemd-detect-virt 2>/dev/null || true",
        ["services"] = "systemctl list-units --type=service --all --no-legend --no-pager --plain 2>/dev/null | head -400",
        ["who"] = "who 2>/dev/null | head -1",
    };

    public static readonly IReadOnlyDictionary<string, string> MacCommands = new Dictionary<string, string>
    {
        ["hostname"] = "hostname",
        ["swvers"] = "sw_vers 2>/dev/null",
        ["hardware"] = "system_profiler SPHardwareDataType 2>/dev/null",
        ["memsize"] = "sysctl -n hw.memsize 2>/dev/null",
        ["cpu"] = "sysctl -n machdep.cpu.brand_string hw.physicalcpu hw.logicalcpu 2>/dev/null",
        ["df"] = "df -P -k 2>/dev/null",
        ["ifconfig"] = "ifconfig 2>/dev/null",
        ["apps"] = "ls -1 /Applications 2>/dev/null | grep '\\.app$'",
        ["boottime"] = "sysctl -n kern.boottime 2>/dev/null",
        ["who"] = "stat -f '%Su' /dev/console 2>/dev/null",
    };

    public static InventoryDocument ParseLinux(IReadOnlyDictionary<string, string> o, string uname, string collectorVersion)
    {
        var doc = new InventoryDocument { Source = "ssh", CollectorVersion = collectorVersion, CollectedAt = DateTimeOffset.UtcNow };
        var unameLines = Lines(uname);
        doc.Os.Family = "linux";
        doc.Os.Architecture = unameLines.ElementAtOrDefault(1);
        doc.Os.Build = unameLines.ElementAtOrDefault(2);

        var host = Get(o, "hostname").Trim();
        if (host.Length > 0)
        {
            doc.Device.Fqdn = host.Contains(".") ? host.ToLowerInvariant() : null;
            doc.Device.Hostname = host.Split('.')[0];
            if (host.Contains(".")) doc.Device.Domain = host.Substring(host.IndexOf('.') + 1).ToLowerInvariant();
        }

        var osr = KeyValues(Get(o, "osrelease"), '=');
        doc.Os.Name = Unquote(osr.TryGet("PRETTY_NAME")) ?? Unquote(osr.TryGet("NAME"));
        doc.Os.Version = Unquote(osr.TryGet("VERSION_ID"));

        // CPU
        var cpu = Get(o, "cpuinfo");
        var models = Regex.Matches(cpu, @"^model name\s*:\s*(.+)$", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value.Trim()).ToList();
        var processors = Regex.Matches(cpu, @"^processor\s*:", RegexOptions.Multiline).Count;
        var coreIds = Regex.Matches(cpu, @"^physical id\s*:\s*(\d+)[\s\S]*?^core id\s*:\s*(\d+)", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value + ":" + m.Groups[2].Value).Distinct().Count();
        doc.Hardware.CpuName = models.FirstOrDefault() ?? Regex.Match(cpu, @"^(?:Hardware|Model)\s*:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim().NullIfEmpty();
        doc.Hardware.CpuThreads = processors > 0 ? processors : null;
        doc.Hardware.CpuCores = coreIds > 0 ? coreIds : doc.Hardware.CpuThreads;

        var mem = Regex.Match(Get(o, "meminfo"), @"^MemTotal:\s*(\d+)\s*kB", RegexOptions.Multiline);
        if (mem.Success) doc.Hardware.MemoryBytes = long.Parse(mem.Groups[1].Value, CultureInfo.InvariantCulture) * 1024;

        var dmi = KeyValues(Get(o, "dmi"), '=');
        doc.Device.Manufacturer = WindowsCollector.Clean(dmi.TryGet("sys_vendor"));
        doc.Device.Model = WindowsCollector.Clean(dmi.TryGet("product_name"));
        doc.Device.SerialNumber = WindowsCollector.Clean(dmi.TryGet("product_serial"));
        doc.Device.SmbiosUuid = WindowsCollector.NormalizeUuid(dmi.TryGet("product_uuid"));
        doc.Hardware.BiosVendor = WindowsCollector.Clean(dmi.TryGet("bios_vendor"));
        doc.Hardware.BiosVersion = WindowsCollector.Clean(dmi.TryGet("bios_version"));
        if (DateTime.TryParseExact(dmi.TryGet("bios_date") ?? "", "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var bd))
            doc.Hardware.BiosDate = new DateTimeOffset(bd.ToUniversalTime());
        if (long.TryParse(dmi.TryGet("chassis_type"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ct))
            doc.Device.Chassis = WindowsCollector.ChassisName(new[] { ct });

        var virt = Get(o, "virt").Trim();
        doc.Hardware.IsVirtual = virt.Length > 0 && virt != "none" ? true : WindowsCollector.IsVirtualModel((doc.Device.Model ?? "") + " " + (doc.Device.Manufacturer ?? "")) ? true : (bool?)(virt == "none" ? false : null);

        foreach (var line in Lines(Get(o, "df")).Skip(1))
        {
            var p = Regex.Split(line.Trim(), @"\s+");
            // With -T: Filesystem Type 1024-blocks Used Available Capacity Mounted
            if (p.Length >= 7 && long.TryParse(p[2], out var size) && long.TryParse(p[4], out var avail))
                doc.Hardware.Volumes.Add(new Volume { Name = p[6], FileSystem = p[1], SizeBytes = size * 1024, FreeBytes = avail * 1024 });
            else if (p.Length >= 6 && long.TryParse(p[1], out var size2) && long.TryParse(p[3], out var avail2))
                doc.Hardware.Volumes.Add(new Volume { Name = p[5], SizeBytes = size2 * 1024, FreeBytes = avail2 * 1024 });
        }

        // Network: "2: eth0    inet 10.0.0.5/24 brd ..." and "2: eth0: <...> ... link/ether aa:bb:..."
        var adapters = new Dictionary<string, NetworkAdapter>();
        foreach (var line in Lines(Get(o, "iplink")))
        {
            var m = Regex.Match(line, @"^\d+:\s*([^:@\s]+)[^\n]*?link/ether\s+([0-9a-fA-F:]{17})");
            if (m.Success && m.Groups[1].Value != "lo") adapters[m.Groups[1].Value] = new NetworkAdapter { Name = m.Groups[1].Value, Mac = WindowsCollector.NormalizeMac(m.Groups[2].Value) };
        }
        foreach (var line in Lines(Get(o, "ipaddr")))
        {
            var m = Regex.Match(line, @"^\d+:\s*(\S+)\s+(inet6?)\s+([0-9a-fA-F\.:]+)/\d+");
            if (!m.Success || m.Groups[1].Value == "lo") continue;
            if (!adapters.TryGetValue(m.Groups[1].Value, out var a)) adapters[m.Groups[1].Value] = a = new NetworkAdapter { Name = m.Groups[1].Value };
            if (m.Groups[2].Value == "inet") a.Ipv4.Add(m.Groups[3].Value);
            else if (!m.Groups[3].Value.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)) a.Ipv6.Add(m.Groups[3].Value);
        }
        var gw = Regex.Match(Get(o, "route"), @"default via ([0-9\.]+) dev (\S+)");
        if (gw.Success && adapters.TryGetValue(gw.Groups[2].Value, out var ga)) ga.Gateway = gw.Groups[1].Value;
        doc.Network.AddRange(adapters.Values.Where(a => a.Mac is not null || a.Ipv4.Count > 0));

        foreach (var line in Lines(Get(o, "packages")))
        {
            var p = line.Split('\t');
            if (p.Length >= 4 && p[1].Length > 0)
                doc.Software.Add(new SoftwareItem { Source = p[0], Name = p[1], Version = p[2].NullIfEmpty(), Architecture = p[3].NullIfEmpty(), Publisher = p.Length > 4 ? p[4].NullIfEmpty() : null });
        }

        var up = Regex.Match(Get(o, "uptime"), @"^(\d+(?:\.\d+)?)");
        if (up.Success && double.TryParse(up.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            doc.Os.LastBoot = doc.CollectedAt.AddSeconds(-secs);

        foreach (var line in Lines(Get(o, "services")))
        {
            var p = Regex.Split(line.Trim(), @"\s+");
            if (p.Length >= 4 && p[0].EndsWith(".service", StringComparison.Ordinal))
                doc.Services.Add(new ServiceItem { Name = p[0].Substring(0, p[0].Length - 8), State = p[3], StartMode = p[1] });
        }
        var who = Get(o, "who").Trim();
        if (who.Length > 0) doc.Users.LoggedOn = who.Split(' ')[0];

        doc.Os.IsServer = true; // Linux hosts reached over SSH are treated as servers unless the chassis says otherwise
        doc.Device.Category = doc.Device.Chassis == "laptop" || doc.Device.Chassis == "desktop" ? "endpoint" : "server";
        if (doc.Device.SerialNumber is null && doc.Device.SmbiosUuid is null) doc.Errors.Add("serial/uuid: readable only by root (/sys/class/dmi/id); use an agent or a sudo-capable account for these fields.");
        return doc;
    }

    public static InventoryDocument ParseMac(IReadOnlyDictionary<string, string> o, string uname, string collectorVersion)
    {
        var doc = new InventoryDocument { Source = "ssh", CollectorVersion = collectorVersion, CollectedAt = DateTimeOffset.UtcNow };
        var unameLines = Lines(uname);
        doc.Os.Family = "macos";
        doc.Os.Architecture = unameLines.ElementAtOrDefault(1);
        doc.Device.Hostname = Get(o, "hostname").Trim().Split('.')[0].NullIfEmpty();
        var sw = KeyValues(Get(o, "swvers"), ':');
        doc.Os.Name = $"{sw.TryGet("ProductName") ?? "macOS"} {sw.TryGet("ProductVersion")}".Trim();
        doc.Os.Version = sw.TryGet("ProductVersion");
        doc.Os.Build = sw.TryGet("BuildVersion");
        var hw = KeyValues(Get(o, "hardware"), ':');
        doc.Device.Manufacturer = "Apple";
        doc.Device.Model = hw.TryGet("Model Name") ?? hw.TryGet("Model Identifier");
        doc.Device.SerialNumber = WindowsCollector.Clean(hw.TryGet("Serial Number (system)"));
        doc.Device.SmbiosUuid = WindowsCollector.NormalizeUuid(hw.TryGet("Hardware UUID"));
        doc.Device.Chassis = (doc.Device.Model ?? "").Contains("Book") ? "laptop" : "desktop";
        if (long.TryParse(Get(o, "memsize").Trim(), out var mem)) doc.Hardware.MemoryBytes = mem;
        var cpu = Lines(Get(o, "cpu"));
        doc.Hardware.CpuName = cpu.ElementAtOrDefault(0) ?? hw.TryGet("Chip") ?? hw.TryGet("Processor Name");
        if (int.TryParse(cpu.ElementAtOrDefault(1), out var phys)) doc.Hardware.CpuCores = phys;
        if (int.TryParse(cpu.ElementAtOrDefault(2), out var log)) doc.Hardware.CpuThreads = log;
        foreach (var line in Lines(Get(o, "df")).Skip(1))
        {
            var p = Regex.Split(line.Trim(), @"\s+");
            if (p.Length >= 6 && p[5] == "/" && long.TryParse(p[1], out var size) && long.TryParse(p[3], out var avail))
                doc.Hardware.Volumes.Add(new Volume { Name = "/", SizeBytes = size * 1024, FreeBytes = avail * 1024 });
        }
        NetworkAdapter? cur = null;
        foreach (var line in Lines(Get(o, "ifconfig")))
        {
            var h = Regex.Match(line, @"^([a-z0-9]+):\s+flags=");
            if (h.Success) { cur = new NetworkAdapter { Name = h.Groups[1].Value }; if (cur.Name != "lo0") doc.Network.Add(cur); continue; }
            if (cur is null) continue;
            var e = Regex.Match(line, @"^\s+ether\s+([0-9a-f:]{17})");
            if (e.Success) cur.Mac = WindowsCollector.NormalizeMac(e.Groups[1].Value);
            var i4 = Regex.Match(line, @"^\s+inet\s+([0-9\.]+)");
            if (i4.Success && i4.Groups[1].Value != "127.0.0.1") cur.Ipv4.Add(i4.Groups[1].Value);
        }
        doc.Network.RemoveAll(n => n.Mac is null && n.Ipv4.Count == 0);
        foreach (var app in Lines(Get(o, "apps")))
            doc.Software.Add(new SoftwareItem { Name = app.EndsWith(".app", StringComparison.Ordinal) ? app.Substring(0, app.Length - 4) : app, Source = "applications" });
        var boot = Regex.Match(Get(o, "boottime"), @"sec = (\d+)");
        if (boot.Success) doc.Os.LastBoot = DateTimeOffset.FromUnixTimeSeconds(long.Parse(boot.Groups[1].Value, CultureInfo.InvariantCulture));
        doc.Users.LoggedOn = Get(o, "who").Trim().NullIfEmpty();
        doc.Device.Category = "endpoint";
        doc.Os.IsServer = false;
        doc.Errors.Add("software: macOS application versions are not collected agentlessly (bundle names only).");
        return doc;
    }

    // ---- helpers ---------------------------------------------------------------------------------
    private static string Get(IReadOnlyDictionary<string, string> o, string k) => o.TryGetValue(k, out var v) ? v ?? "" : "";
    private static List<string> Lines(string s) => s.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
    private static string? Unquote(string? s) => s?.Trim().Trim('"', '\'').NullIfEmpty();

    private static Dictionary<string, string> KeyValues(string text, char sep)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(text))
        {
            var i = line.IndexOf(sep);
            if (i <= 0) continue;
            var k = line.Substring(0, i).Trim();
            if (!d.ContainsKey(k)) d[k] = line.Substring(i + 1).Trim();
        }
        return d;
    }

    internal static string? TryGet(this Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
    internal static string? NullIfEmpty(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
