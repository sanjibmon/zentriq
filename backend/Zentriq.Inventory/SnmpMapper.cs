using System;
using System.Collections.Generic;
using System.Linq;

namespace Zentriq.Inventory;

/// <summary>
/// Network devices (switches, routers, firewalls, printers, UPS) over SNMP. The collector performs GET /
/// bounded WALK operations on the standard MIBs below and hands the values here; mapping is pure.
/// </summary>
public static class SnmpMapper
{
    public const string SysDescr = "1.3.6.1.2.1.1.1.0";
    public const string SysObjectId = "1.3.6.1.2.1.1.2.0";
    public const string SysUpTime = "1.3.6.1.2.1.1.3.0";
    public const string SysContact = "1.3.6.1.2.1.1.4.0";
    public const string SysName = "1.3.6.1.2.1.1.5.0";
    public const string SysLocation = "1.3.6.1.2.1.1.6.0";
    public static readonly string[] SystemOids = { SysDescr, SysObjectId, SysUpTime, SysContact, SysName, SysLocation };

    // Columns walked (bounded) by the collector.
    public const string EntPhysicalClass = "1.3.6.1.2.1.47.1.1.1.1.5";        // 3 = chassis
    public const string EntPhysicalSoftwareRev = "1.3.6.1.2.1.47.1.1.1.1.10";
    public const string EntPhysicalSerialNum = "1.3.6.1.2.1.47.1.1.1.1.11";
    public const string EntPhysicalMfgName = "1.3.6.1.2.1.47.1.1.1.1.12";
    public const string EntPhysicalModelName = "1.3.6.1.2.1.47.1.1.1.1.13";
    public const string IfDescr = "1.3.6.1.2.1.2.2.1.2";
    public const string IfPhysAddress = "1.3.6.1.2.1.2.2.1.6";
    public const string IfSpeed = "1.3.6.1.2.1.2.2.1.5";
    public const string IpAdEntAddr = "1.3.6.1.2.1.4.20.1.1";
    public const string PrtGeneralSerial = "1.3.6.1.2.1.43.5.1.1.17";           // Printer-MIB prtGeneralSerialNumber
    public const string HrDeviceDescr = "1.3.6.1.2.1.25.3.2.1.3";
    public const string UpsIdentModel = "1.3.6.1.2.1.33.1.1.2.0";
    public static readonly string[] WalkColumns = { EntPhysicalClass, EntPhysicalSoftwareRev, EntPhysicalSerialNum, EntPhysicalMfgName, EntPhysicalModelName, IfDescr, IfPhysAddress, IfSpeed, IpAdEntAddr, PrtGeneralSerial };

    private static readonly Dictionary<string, string> Enterprises = new()
    {
        ["9"] = "Cisco", ["2636"] = "Juniper Networks", ["11"] = "HP", ["47196"] = "HPE Aruba", ["14823"] = "Aruba", ["25506"] = "H3C",
        ["2011"] = "Huawei", ["1916"] = "Extreme Networks", ["12356"] = "Fortinet", ["25461"] = "Palo Alto Networks", ["41112"] = "Ubiquiti",
        ["14988"] = "MikroTik", ["30065"] = "Arista", ["3375"] = "F5", ["4526"] = "Netgear", ["171"] = "D-Link", ["11863"] = "TP-Link",
        ["674"] = "Dell", ["6027"] = "Dell (Force10)", ["2620"] = "Check Point", ["8741"] = "SonicWall", ["12325"] = "pfSense/FreeBSD",
        ["367"] = "Ricoh", ["1602"] = "Canon", ["2435"] = "Brother", ["1347"] = "Kyocera", ["253"] = "Xerox", ["18334"] = "Konica Minolta",
        ["1248"] = "Epson", ["641"] = "Lexmark", ["2385"] = "Sharp", ["318"] = "APC (Schneider Electric)", ["534"] = "Eaton",
        ["6876"] = "VMware", ["8072"] = "Net-SNMP", ["311"] = "Microsoft", ["5624"] = "Enterasys", ["6486"] = "Alcatel-Lucent", ["207"] = "Allied Telesis",
        ["9303"] = "Ruckus", ["25053"] = "Ruckus", ["29671"] = "Cisco Meraki", ["35098"] = "Sophos", ["21067"] = "Sophos", ["10002"] = "Ubiquiti",
    };

    public static InventoryDocument Map(IReadOnlyDictionary<string, string> v, string ip, string collectorVersion)
    {
        var doc = new InventoryDocument { Source = "snmp", CollectorVersion = collectorVersion, CollectedAt = DateTimeOffset.UtcNow };
        var descr = Get(v, SysDescr);
        var oid = Get(v, SysObjectId)?.TrimStart('.');
        var vendor = Vendor(oid);
        doc.Device.Hostname = Get(v, SysName)?.Split('.')[0];
        doc.Device.Fqdn = Get(v, SysName) is { } n && n.Contains(".") ? n.ToLowerInvariant() : null;
        doc.Device.Description = descr;
        doc.Device.Location = Get(v, SysLocation);
        doc.Device.Contact = Get(v, SysContact);

        // Chassis entity (entPhysicalClass = 3) carries model/serial/firmware.
        var chassisIdx = v.Where(kv => kv.Key.StartsWith(EntPhysicalClass + ".", StringComparison.Ordinal) && kv.Value == "3")
                          .Select(kv => kv.Key.Substring(EntPhysicalClass.Length + 1)).FirstOrDefault();
        string? Ent(string col) => chassisIdx is null ? null : WindowsCollector.Clean(Get(v, col + "." + chassisIdx));
        doc.Device.Manufacturer = Ent(EntPhysicalMfgName) ?? vendor;
        doc.Device.Model = Ent(EntPhysicalModelName) ?? Get(v, UpsIdentModel);
        doc.Device.SerialNumber = Ent(EntPhysicalSerialNum) ?? WindowsCollector.Clean(FirstUnder(v, PrtGeneralSerial));
        doc.Os.Version = Ent(EntPhysicalSoftwareRev);

        var lower = (descr ?? "").ToLowerInvariant();
        var isPrinter = v.Keys.Any(k => k.StartsWith(PrtGeneralSerial, StringComparison.Ordinal)) || lower.Contains("printer") || lower.Contains("laserjet") || lower.Contains("mfp");
        var isUps = v.ContainsKey(UpsIdentModel) || lower.Contains("ups") && (vendor ?? "").Contains("APC");
        var isHostOs = oid is not null && (oid.StartsWith("1.3.6.1.4.1.8072", StringComparison.Ordinal) || oid.StartsWith("1.3.6.1.4.1.311", StringComparison.Ordinal));
        if (isPrinter) { doc.Device.Category = "printer"; doc.Device.Chassis = "printer"; doc.Os.Family = "other"; }
        else if (isUps) { doc.Device.Category = "other"; doc.Device.Chassis = "other"; doc.Os.Family = "other"; }
        else if (isHostOs)
        {
            doc.Device.Category = "server";
            doc.Os.Family = lower.Contains("windows") ? "windows" : lower.Contains("linux") ? "linux" : "other";
        }
        else { doc.Device.Category = "network_device"; doc.Device.Chassis = "network"; doc.Os.Family = "network"; }
        doc.Os.Name = OsName(descr, vendor);

        if (long.TryParse(Get(v, SysUpTime), out var ticks) && ticks > 0) doc.Os.LastBoot = doc.CollectedAt.AddMilliseconds(-ticks * 10.0);

        // Interfaces: MACs (ifPhysAddress) and IPv4 addresses.
        var ifIdx = v.Keys.Where(k => k.StartsWith(IfDescr + ".", StringComparison.Ordinal)).Select(k => k.Substring(IfDescr.Length + 1)).Take(512);
        foreach (var i in ifIdx)
        {
            var mac = WindowsCollector.NormalizeMac(Get(v, IfPhysAddress + "." + i));
            if (mac is null) continue;
            doc.Network.Add(new NetworkAdapter { Name = Get(v, IfDescr + "." + i), Mac = mac, SpeedMbps = long.TryParse(Get(v, IfSpeed + "." + i), out var sp) ? sp / 1_000_000 : null });
        }
        var ips = v.Where(kv => kv.Key.StartsWith(IpAdEntAddr + ".", StringComparison.Ordinal)).Select(kv => kv.Value).Where(a => a != "127.0.0.1").Distinct().ToList();
        if (doc.Network.Count > 0) doc.Network[0].Ipv4.AddRange(ips);
        else doc.Network.Add(new NetworkAdapter { Name = "management", Ipv4 = ips.Count > 0 ? ips : new List<string> { ip } });
        return doc;
    }

    public static string? Vendor(string? sysObjectId)
    {
        if (sysObjectId is null) return null;
        const string prefix = "1.3.6.1.4.1.";
        if (!sysObjectId.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var ent = sysObjectId.Substring(prefix.Length).Split('.')[0];
        return Enterprises.TryGetValue(ent, out var name) ? name : $"Enterprise {ent}";
    }

    private static string? OsName(string? descr, string? vendor)
    {
        if (string.IsNullOrWhiteSpace(descr)) return vendor;
        var first = descr!.Replace("\r", "").Split('\n')[0].Trim();
        return first.Length > 120 ? first.Substring(0, 120) : first;
    }

    private static string? Get(IReadOnlyDictionary<string, string> v, string oid) =>
        v.TryGetValue(oid, out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static string? FirstUnder(IReadOnlyDictionary<string, string> v, string column) =>
        v.Where(kv => kv.Key.StartsWith(column + ".", StringComparison.Ordinal)).Select(kv => kv.Value).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
}
