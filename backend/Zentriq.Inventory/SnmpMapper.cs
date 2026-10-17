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
    public const string SysServices = "1.3.6.1.2.1.1.7.0";
    public const string IpForwarding = "1.3.6.1.2.1.4.1.0";                    // 1 = forwarding (router)
    public const string Dot1dBaseBridgeAddress = "1.3.6.1.2.1.17.1.1.0";       // present on switches
    public const string Dot1dBaseNumPorts = "1.3.6.1.2.1.17.1.2.0";
    public static readonly string[] SystemOids = { SysDescr, SysObjectId, SysUpTime, SysContact, SysName, SysLocation, SysServices, IpForwarding, Dot1dBaseBridgeAddress, Dot1dBaseNumPorts };

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
    public const string EntPhysicalFirmwareRev = "1.3.6.1.2.1.47.1.1.1.1.9";
    public const string IfType = "1.3.6.1.2.1.2.2.1.3";                         // 6 / 117 = Ethernet
    public const string IfOperStatus = "1.3.6.1.2.1.2.2.1.8";                   // 1 up, 2 down
    public const string IfAlias = "1.3.6.1.2.1.31.1.1.1.18";
    public const string PrtMarkerLifeCount = "1.3.6.1.2.1.43.10.2.1.4";         // pages printed
    public const string PrtSuppliesDescription = "1.3.6.1.2.1.43.11.1.1.6";
    public const string PrtSuppliesMaxCapacity = "1.3.6.1.2.1.43.11.1.1.8";
    public const string PrtSuppliesLevel = "1.3.6.1.2.1.43.11.1.1.9";
    public const string HrPrinterStatus = "1.3.6.1.2.1.25.3.5.1.1";
    public static readonly string[] WalkColumns = { EntPhysicalClass, EntPhysicalSoftwareRev, EntPhysicalSerialNum, EntPhysicalMfgName, EntPhysicalModelName, EntPhysicalFirmwareRev,
        IfDescr, IfPhysAddress, IfSpeed, IfType, IfOperStatus, IfAlias, IpAdEntAddr, PrtGeneralSerial, PrtMarkerLifeCount, PrtSuppliesDescription, PrtSuppliesMaxCapacity, PrtSuppliesLevel,
        HrPrinterStatus, HrDeviceDescr };

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
        ["3097"] = "WatchGuard", ["7244"] = "Barracuda", ["890"] = "Zyxel", ["6574"] = "Synology", ["24681"] = "QNAP", ["39165"] = "Hikvision",
        ["1588"] = "Brocade", ["3076"] = "Cisco (Altiga)", ["2604"] = "Sophos (Cyberoam)", ["17713"] = "Cambium", ["4413"] = "Broadcom", ["3808"] = "CyberPower",
        ["1991"] = "Brocade (Foundry)", ["13464"] = "Grandstream", ["37459"] = "Yealink", ["13885"] = "Polycom",
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
        var hasPrinterMib = v.Keys.Any(k => k.StartsWith(PrtGeneralSerial, StringComparison.Ordinal) || k.StartsWith(PrtMarkerLifeCount, StringComparison.Ordinal) || k.StartsWith(HrPrinterStatus, StringComparison.Ordinal));
        var isUps = v.ContainsKey(UpsIdentModel);
        var isHostOs = oid is not null && (oid.StartsWith("1.3.6.1.4.1.8072", StringComparison.Ordinal) || oid.StartsWith("1.3.6.1.4.1.311", StringComparison.Ordinal))
                       && !hasPrinterMib && !lower.Contains("pfsense") && !lower.Contains("opnsense");
        var services = int.TryParse(Get(v, SysServices), out var sv) ? sv : (int?)null;
        bool? forwarding = Get(v, IpForwarding) is { } fw ? fw == "1" : null;
        var hasBridge = v.ContainsKey(Dot1dBaseBridgeAddress) || v.ContainsKey(Dot1dBaseNumPorts);
        var fp = isUps
            ? new Fingerprint { DeviceType = "ups", Manufacturer = vendor, Evidence = { "UPS-MIB answered" } }
            : DeviceFingerprint.FromSnmp(descr, vendor, services, forwarding, hasBridge, hasPrinterMib, isHostOs);
        if (fp.Manufacturer is not null && (doc.Device.Manufacturer is null || doc.Device.Manufacturer.StartsWith("Enterprise ", StringComparison.Ordinal))) doc.Device.Manufacturer = fp.Manufacturer;
        doc.Device.IdentifiedBy = fp.Evidence.Count > 0 ? fp.Evidence : null;
        if (isHostOs)
        {
            doc.Device.Category = "server";
            doc.Os.Family = lower.Contains("windows") ? "windows" : lower.Contains("linux") ? "linux" : "other";
        }
        else
        {
            var type = fp.DeviceType ?? "network";
            doc.Device.Chassis = type;
            doc.Device.Category = DeviceTypes.CategoryFor(type) ?? "network_device";
            doc.Os.Family = DeviceTypes.CategoryFor(type) == "network_device" ? "network" : "other";
        }
        doc.Os.Version ??= Ent(EntPhysicalFirmwareRev);

        if (hasPrinterMib)
        {
            // Model: the printer's own description (hrDeviceDescr of the printer device) when ENTITY-MIB has none.
            doc.Device.Model ??= WindowsCollector.Clean(v.Where(kv => kv.Key.StartsWith(HrDeviceDescr + ".", StringComparison.Ordinal)).Select(kv => kv.Value)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x.IndexOf("network", StringComparison.OrdinalIgnoreCase) < 0));
            var p = new PrinterInfo();
            var counts = v.Where(kv => kv.Key.StartsWith(PrtMarkerLifeCount + ".", StringComparison.Ordinal)).Select(kv => long.TryParse(kv.Value, out var n) ? n : 0).ToList();
            if (counts.Count > 0 && counts.Max() > 0) p.PageCount = counts.Max();
            p.Status = v.Where(kv => kv.Key.StartsWith(HrPrinterStatus + ".", StringComparison.Ordinal)).Select(kv => kv.Value).FirstOrDefault() switch
            {
                "3" => "idle", "4" => "printing", "5" => "warmup", "1" => "other", null => null, _ => "unknown",
            };
            foreach (var kv in v.Where(kv => kv.Key.StartsWith(PrtSuppliesDescription + ".", StringComparison.Ordinal)).Take(24))
            {
                var idx = kv.Key.Substring(PrtSuppliesDescription.Length + 1);
                int? level = null;
                if (long.TryParse(Get(v, PrtSuppliesLevel + "." + idx), out var lv) && long.TryParse(Get(v, PrtSuppliesMaxCapacity + "." + idx), out var max) && max > 0 && lv >= 0)
                    level = (int)Math.Min(100, Math.Round(lv * 100.0 / max));
                else if (lv == -3) level = 100; // "some remaining"
                var name = WindowsCollector.Clean(kv.Value);
                if (name is not null) p.Supplies.Add(new PrinterSupply { Name = name, LevelPercent = level });
            }
            doc.Printer = p;
        }
        doc.Os.Name = OsName(descr, vendor);

        if (long.TryParse(Get(v, SysUpTime), out var ticks) && ticks > 0) doc.Os.LastBoot = doc.CollectedAt.AddMilliseconds(-ticks * 10.0);

        // Interfaces: MACs (ifPhysAddress) and IPv4 addresses.
        var ifIdx = v.Keys.Where(k => k.StartsWith(IfDescr + ".", StringComparison.Ordinal)).Select(k => k.Substring(IfDescr.Length + 1)).Take(512);
        foreach (var i in ifIdx)
        {
            var mac = WindowsCollector.NormalizeMac(Get(v, IfPhysAddress + "." + i));
            if (mac is null) continue;
            doc.Network.Add(new NetworkAdapter
            {
                Name = Get(v, IfDescr + "." + i), Mac = mac, SpeedMbps = long.TryParse(Get(v, IfSpeed + "." + i), out var sp) ? sp / 1_000_000 : null,
                Alias = Get(v, IfAlias + "." + i),
                Status = Get(v, IfOperStatus + "." + i) switch { "1" => "up", "2" => "down", null => null, _ => "other" },
            });
        }
        if (!isHostOs && (DeviceTypes.CategoryFor(doc.Device.Chassis) == "network_device"))
        {
            var ethernet = v.Where(kv => kv.Key.StartsWith(IfType + ".", StringComparison.Ordinal) && kv.Value is "6" or "117" or "62" or "69")
                            .Select(kv => kv.Key.Substring(IfType.Length + 1)).ToList();
            doc.Ports = new PortSummary
            {
                Physical = ethernet.Count,
                Up = ethernet.Count(i => Get(v, IfOperStatus + "." + i) == "1"),
                Routing = forwarding,
                Switching = hasBridge ? true : null,
            };
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
