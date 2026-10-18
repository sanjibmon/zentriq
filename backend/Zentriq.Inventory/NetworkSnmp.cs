using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Zentriq.Inventory;

/// <summary>
/// Reads the network inventory of a switch, router, firewall or access point from SNMP values (OID → value, octet
/// strings that are not text arrive as hex). Standard MIBs first, Cisco MIBs where the standard ones are missing.
/// </summary>
public static class NetworkSnmp
{
    // IF-MIB / EtherLike-MIB
    public const string IfMtu = "1.3.6.1.2.1.2.2.1.4";
    public const string IfAdminStatus = "1.3.6.1.2.1.2.2.1.7";
    public const string IfLastChange = "1.3.6.1.2.1.2.2.1.9";
    public const string IfInErrors = "1.3.6.1.2.1.2.2.1.14";
    public const string IfOutErrors = "1.3.6.1.2.1.2.2.1.20";
    public const string IfName = "1.3.6.1.2.1.31.1.1.1.1";
    public const string IfHighSpeed = "1.3.6.1.2.1.31.1.1.1.15";
    public const string Dot3Duplex = "1.3.6.1.2.1.10.7.2.1.19";
    // IP-MIB
    public const string IpAdEntIfIndex = "1.3.6.1.2.1.4.20.1.2";
    public const string IpNetToMediaPhys = "1.3.6.1.2.1.4.22.1.2";
    public const string IpCidrRouteNumber = "1.3.6.1.2.1.4.24.3.0";
    public const string InetCidrRouteNumber = "1.3.6.1.2.1.4.24.6.0";
    // BRIDGE-MIB / Q-BRIDGE-MIB
    public const string Dot1dBasePortIfIndex = "1.3.6.1.2.1.17.1.4.1.2";
    public const string Dot1dTpFdbPort = "1.3.6.1.2.1.17.4.3.1.2";
    public const string Dot1qTpFdbPort = "1.3.6.1.2.1.17.7.1.2.2.1.2";
    public const string Dot1qVlanStaticName = "1.3.6.1.2.1.17.7.1.4.3.1.1";
    public const string Dot1qPvid = "1.3.6.1.2.1.17.7.1.4.5.1.1";
    public const string Dot1dStpDesignatedRoot = "1.3.6.1.2.1.17.2.5.0";
    // LLDP-MIB
    public const string LldpLocSysCapEnabled = "1.0.8802.1.1.2.1.3.6.0";
    public const string LldpLocPortId = "1.0.8802.1.1.2.1.3.7.1.3";
    public const string LldpLocPortDesc = "1.0.8802.1.1.2.1.3.7.1.4";
    public const string LldpRemChassisIdSubtype = "1.0.8802.1.1.2.1.4.1.1.4";
    public const string LldpRemChassisId = "1.0.8802.1.1.2.1.4.1.1.5";
    public const string LldpRemPortId = "1.0.8802.1.1.2.1.4.1.1.7";
    public const string LldpRemPortDesc = "1.0.8802.1.1.2.1.4.1.1.8";
    public const string LldpRemSysName = "1.0.8802.1.1.2.1.4.1.1.9";
    public const string LldpRemSysDesc = "1.0.8802.1.1.2.1.4.1.1.10";
    public const string LldpRemSysCapEnabled = "1.0.8802.1.1.2.1.4.1.1.12";
    public const string LldpRemManAddrIfSubtype = "1.0.8802.1.1.2.1.4.2.1.3";
    // CISCO-CDP-MIB, CISCO-VTP-MIB, CISCO-VLAN-MEMBERSHIP-MIB, CISCO-PROCESS-MIB
    public const string CdpCacheAddress = "1.3.6.1.4.1.9.9.23.1.2.1.1.4";
    public const string CdpCacheDeviceId = "1.3.6.1.4.1.9.9.23.1.2.1.1.6";
    public const string CdpCacheDevicePort = "1.3.6.1.4.1.9.9.23.1.2.1.1.7";
    public const string CdpCachePlatform = "1.3.6.1.4.1.9.9.23.1.2.1.1.8";
    public const string CdpCacheCapabilities = "1.3.6.1.4.1.9.9.23.1.2.1.1.9";
    public const string VtpVlanName = "1.3.6.1.4.1.9.9.46.1.3.1.1.4";
    public const string VlanTrunkPortDynamicStatus = "1.3.6.1.4.1.9.9.46.1.6.1.1.14";
    public const string CiscoVmVlan = "1.3.6.1.4.1.9.9.68.1.2.2.1.2";
    public const string CiscoCpu5Min = "1.3.6.1.4.1.9.9.109.1.1.1.1.8";
    // ENTITY-MIB, POWER-ETHERNET-MIB, HOST-RESOURCES-MIB
    public const string EntPhysicalDescr = "1.3.6.1.2.1.47.1.1.1.1.2";
    public const string EntPhysicalName = "1.3.6.1.2.1.47.1.1.1.1.7";
    public const string PethMainPsePower = "1.3.6.1.2.1.105.1.3.1.1.2";
    public const string PethMainPseConsumption = "1.3.6.1.2.1.105.1.3.1.1.4";
    public const string PethPsePortDetectionStatus = "1.3.6.1.2.1.105.1.1.1.6";
    public const string HrProcessorLoad = "1.3.6.1.2.1.25.3.3.1.2";
    // Traffic, discards, spanning tree per port, link aggregation
    public const string IfInDiscards = "1.3.6.1.2.1.2.2.1.13";
    public const string IfOutDiscards = "1.3.6.1.2.1.2.2.1.19";
    public const string IfHcInOctets = "1.3.6.1.2.1.31.1.1.1.6";
    public const string IfHcOutOctets = "1.3.6.1.2.1.31.1.1.1.10";
    public const string Dot1dStpPortState = "1.3.6.1.2.1.17.2.15.1.3";
    public const string Dot3adAggPortAttachedAggId = "1.2.840.10006.300.43.1.2.1.1.13";
    // Memory: HOST-RESOURCES-MIB storage, CISCO-MEMORY-POOL-MIB
    public const string HrStorageType = "1.3.6.1.2.1.25.2.3.1.2";
    public const string HrStorageUnits = "1.3.6.1.2.1.25.2.3.1.4";
    public const string HrStorageSize = "1.3.6.1.2.1.25.2.3.1.5";
    public const string HrStorageUsed = "1.3.6.1.2.1.25.2.3.1.6";
    public const string HrStorageRam = "1.3.6.1.2.1.25.2.1.2";
    public const string CiscoMemPoolUsed = "1.3.6.1.4.1.9.9.48.1.1.1.5";
    public const string CiscoMemPoolFree = "1.3.6.1.4.1.9.9.48.1.1.1.6";
    // ENTITY-SENSOR-MIB
    public const string EntPhySensorType = "1.3.6.1.2.1.99.1.1.1.1";
    public const string EntPhySensorScale = "1.3.6.1.2.1.99.1.1.1.2";
    public const string EntPhySensorPrecision = "1.3.6.1.2.1.99.1.1.1.3";
    public const string EntPhySensorValue = "1.3.6.1.2.1.99.1.1.1.4";
    public const string EntPhySensorOperStatus = "1.3.6.1.2.1.99.1.1.1.5";
    // CISCO-ENVMON-MIB: fans, power supplies, temperature
    public const string CiscoEnvFanDescr = "1.3.6.1.4.1.9.9.13.1.4.1.2";
    public const string CiscoEnvFanState = "1.3.6.1.4.1.9.9.13.1.4.1.3";
    public const string CiscoEnvSupplyDescr = "1.3.6.1.4.1.9.9.13.1.5.1.2";
    public const string CiscoEnvSupplyState = "1.3.6.1.4.1.9.9.13.1.5.1.3";
    public const string CiscoEnvTempDescr = "1.3.6.1.4.1.9.9.13.1.3.1.2";
    public const string CiscoEnvTempValue = "1.3.6.1.4.1.9.9.13.1.3.1.3";
    public const string CiscoEnvTempState = "1.3.6.1.4.1.9.9.13.1.3.1.6";
    // BGP4-MIB, OSPF-MIB
    public const string BgpPeerState = "1.3.6.1.2.1.15.3.1.2";
    public const string BgpPeerRemoteAs = "1.3.6.1.2.1.15.3.1.9";
    public const string OspfNbrState = "1.3.6.1.2.1.14.10.1.6";

    /// <summary>Scalars read with GET.</summary>
    public static readonly string[] GetOids = { IpCidrRouteNumber, InetCidrRouteNumber, Dot1dStpDesignatedRoot, LldpLocSysCapEnabled };

    /// <summary>Tables walked, with the most rows kept per column (MAC and ARP tables can be large).</summary>
    public static readonly IReadOnlyDictionary<string, int> WalkColumns = new Dictionary<string, int>
    {
        [IfMtu] = 1024, [IfAdminStatus] = 1024, [IfLastChange] = 1024, [IfInErrors] = 1024, [IfOutErrors] = 1024,
        [IfName] = 1024, [IfHighSpeed] = 1024, [Dot3Duplex] = 1024, [IpAdEntIfIndex] = 512, [IpNetToMediaPhys] = 4096,
        [Dot1dBasePortIfIndex] = 1024, [Dot1dTpFdbPort] = 8192, [Dot1qTpFdbPort] = 8192, [Dot1qVlanStaticName] = 4094, [Dot1qPvid] = 1024,
        [LldpLocPortId] = 1024, [LldpLocPortDesc] = 1024, [LldpRemChassisIdSubtype] = 1024, [LldpRemChassisId] = 1024, [LldpRemPortId] = 1024, [LldpRemPortDesc] = 1024,
        [LldpRemSysName] = 1024, [LldpRemSysDesc] = 1024, [LldpRemSysCapEnabled] = 1024, [LldpRemManAddrIfSubtype] = 1024,
        [CdpCacheAddress] = 1024, [CdpCacheDeviceId] = 1024, [CdpCacheDevicePort] = 1024, [CdpCachePlatform] = 1024, [CdpCacheCapabilities] = 1024,
        [VtpVlanName] = 4094, [VlanTrunkPortDynamicStatus] = 1024, [CiscoVmVlan] = 1024, [CiscoCpu5Min] = 16,
        [EntPhysicalDescr] = 2048, [EntPhysicalName] = 2048, [PethMainPsePower] = 16, [PethMainPseConsumption] = 16, [PethPsePortDetectionStatus] = 1024,
        [HrProcessorLoad] = 64,
        [IfInDiscards] = 1024, [IfOutDiscards] = 1024, [IfHcInOctets] = 1024, [IfHcOutOctets] = 1024, [Dot1dStpPortState] = 1024, [Dot3adAggPortAttachedAggId] = 1024,
        [HrStorageType] = 64, [HrStorageUnits] = 64, [HrStorageSize] = 64, [HrStorageUsed] = 64, [CiscoMemPoolUsed] = 32, [CiscoMemPoolFree] = 32,
        [EntPhySensorType] = 512, [EntPhySensorScale] = 512, [EntPhySensorPrecision] = 512, [EntPhySensorValue] = 512, [EntPhySensorOperStatus] = 512,
        [CiscoEnvFanDescr] = 64, [CiscoEnvFanState] = 64, [CiscoEnvSupplyDescr] = 64, [CiscoEnvSupplyState] = 64,
        [CiscoEnvTempDescr] = 64, [CiscoEnvTempValue] = 64, [CiscoEnvTempState] = 64,
        [BgpPeerState] = 512, [BgpPeerRemoteAs] = 512, [OspfNbrState] = 512,
    };

    /// <summary>Octet strings that are binary: always delivered as hex.</summary>
    public static readonly string[] HexColumns = { SnmpMapper.IfPhysAddress, IpNetToMediaPhys, LldpRemChassisId, LldpRemSysCapEnabled, LldpLocSysCapEnabled, CdpCacheAddress, CdpCacheCapabilities,
        Dot1dStpDesignatedRoot, SnmpMapper.Dot1dBaseBridgeAddress };

    public static NetworkDeviceInfo Map(IReadOnlyDictionary<string, string> v)
    {
        var n = new NetworkDeviceInfo();

        // ---- interfaces ----------------------------------------------------------------------------------
        var byIndex = new Dictionary<int, NetInterface>();
        foreach (var kv in Column(v, SnmpMapper.IfDescr).Take(1024))
        {
            if (!int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var i)) continue;
            string? Col(string c) => Get(v, c + "." + kv.Key);
            var high = Long(Col(IfHighSpeed));
            var low = Long(Col(SnmpMapper.IfSpeed));
            var it = new NetInterface
            {
                Index = i, Description = Clean(kv.Value), Name = Clean(Col(IfName)) ?? Clean(kv.Value), Alias = Clean(Col(SnmpMapper.IfAlias)),
                Type = IfTypeName(Col(SnmpMapper.IfType)), Mac = WindowsCollector.NormalizeMac(Col(SnmpMapper.IfPhysAddress)),
                Admin = Col(IfAdminStatus) switch { "1" => "up", "2" => "down", "3" => "testing", null => null, _ => "other" },
                Oper = Col(SnmpMapper.IfOperStatus) switch { "1" => "up", "2" => "down", "3" => "testing", "5" => "dormant", "6" => "notPresent", "7" => "lowerLayerDown", null => null, _ => "unknown" },
                SpeedMbps = high is > 0 ? high : low is > 0 ? low / 1_000_000 : null,
                Duplex = Col(Dot3Duplex) switch { "2" => "half", "3" => "full", "1" => "unknown", _ => null },
                Mtu = (int?)Long(Col(IfMtu)), InErrors = Long(Col(IfInErrors)), OutErrors = Long(Col(IfOutErrors)),
                LastChangeSeconds = Long(Col(IfLastChange)) is { } lc ? lc / 100 : null,
                InOctets = Long(Col(IfHcInOctets)), OutOctets = Long(Col(IfHcOutOctets)),
                InDiscards = Long(Col(IfInDiscards)), OutDiscards = Long(Col(IfOutDiscards)),
            };
            byIndex[i] = it;
            n.Interfaces.Add(it);
        }
        foreach (var kv in Column(v, IpAdEntIfIndex))
            if (int.TryParse(kv.Value, out var i) && byIndex.TryGetValue(i, out var it) && kv.Key != "127.0.0.1") it.Ipv4.Add(kv.Key);

        // ---- bridge ports, VLANs, MAC address table ---------------------------------------------------------
        var portToIf = new Dictionary<int, int>();
        foreach (var kv in Column(v, Dot1dBasePortIfIndex))
            if (int.TryParse(kv.Key, out var bp) && int.TryParse(kv.Value, out var ifi)) portToIf[bp] = ifi;
        int? IfOfPort(int bridgePort) => portToIf.TryGetValue(bridgePort, out var ifi) ? ifi : byIndex.ContainsKey(bridgePort) ? bridgePort : (int?)null;

        foreach (var kv in Column(v, Dot1qVlanStaticName))
            if (int.TryParse(kv.Key, out var id)) n.Vlans.Add(new NetVlan { Id = id, Name = Clean(kv.Value) });
        foreach (var kv in Column(v, VtpVlanName))
        {
            var parts = kv.Key.Split('.');
            if (parts.Length == 2 && int.TryParse(parts[1], out var id) && id < 1002 && n.Vlans.All(x => x.Id != id)) n.Vlans.Add(new NetVlan { Id = id, Name = Clean(kv.Value) });
        }
        n.Vlans = n.Vlans.OrderBy(x => x.Id).ToList();
        foreach (var kv in Column(v, Dot1qPvid))
            if (int.TryParse(kv.Key, out var bp) && int.TryParse(kv.Value, out var pvid) && IfOfPort(bp) is { } ifi && byIndex.TryGetValue(ifi, out var it)) it.Vlan = pvid;
        foreach (var kv in Column(v, CiscoVmVlan))
            if (int.TryParse(kv.Key, out var ifi) && int.TryParse(kv.Value, out var vl) && byIndex.TryGetValue(ifi, out var it)) it.Vlan ??= vl;
        foreach (var kv in Column(v, VlanTrunkPortDynamicStatus))
            if (int.TryParse(kv.Key, out var ifi) && byIndex.TryGetValue(ifi, out var it) && kv.Value == "1") it.Trunk = true;

        // Q-BRIDGE: index = vlan.m1.m2.m3.m4.m5.m6 ; BRIDGE: index = m1..m6 (no VLAN).
        var seen = new HashSet<string>();
        foreach (var kv in Column(v, Dot1qTpFdbPort))
        {
            var p = kv.Key.Split('.');
            if (p.Length != 7 || !int.TryParse(kv.Value, out var bp) || bp == 0) continue;
            var mac = MacFromIndex(p, 1);
            if (mac is null) continue;
            int.TryParse(p[0], out var vlan);
            if (seen.Add(mac + "/" + vlan)) n.MacTable.Add(new NetMacEntry { Mac = mac, Vlan = vlan, IfIndex = IfOfPort(bp) });
        }
        if (n.MacTable.Count == 0)
            foreach (var kv in Column(v, Dot1dTpFdbPort))
            {
                var p = kv.Key.Split('.');
                if (p.Length != 6 || !int.TryParse(kv.Value, out var bp) || bp == 0) continue;
                var mac = MacFromIndex(p, 0);
                if (mac is not null && seen.Add(mac)) n.MacTable.Add(new NetMacEntry { Mac = mac, IfIndex = IfOfPort(bp) });
            }
        // Trunk: a port on which MACs were learned in more than one VLAN.
        foreach (var g in n.MacTable.Where(m => m.IfIndex is not null && m.Vlan is > 0).GroupBy(m => m.IfIndex!.Value))
            if (g.Select(m => m.Vlan).Distinct().Count() > 1 && byIndex.TryGetValue(g.Key, out var it)) it.Trunk = true;

        // ---- ARP -------------------------------------------------------------------------------------------
        foreach (var kv in Column(v, IpNetToMediaPhys).Take(4096))
        {
            var p = kv.Key.Split('.');
            if (p.Length != 5) continue;
            var mac = WindowsCollector.NormalizeMac(kv.Value);
            if (mac is null || mac == "FF:FF:FF:FF:FF:FF") continue;
            n.Arp.Add(new NetArpEntry { Ip = string.Join(".", p, 1, 4), Mac = mac, IfIndex = int.TryParse(p[0], out var ifi) ? ifi : (int?)null });
        }

        // ---- neighbours: LLDP, then CDP --------------------------------------------------------------------
        var localPort = new Dictionary<string, string?>();
        foreach (var kv in Column(v, LldpLocPortId)) localPort[kv.Key] = Clean(kv.Value);
        foreach (var kv in Column(v, LldpLocPortDesc)) if (!localPort.ContainsKey(kv.Key) || IsHexLike(localPort[kv.Key])) localPort[kv.Key] = Clean(kv.Value);
        // lldpRemManAddr index: timeMark.localPort.remIndex.subtype(1 = IPv4).length(4).a.b.c.d
        var manAddr = new Dictionary<string, string>();
        foreach (var kv in Column(v, LldpRemManAddrIfSubtype))
        {
            var p = kv.Key.Split('.');
            if (p.Length >= 9 && p[3] == "1" && p[4] == "4") manAddr[p[0] + "." + p[1] + "." + p[2]] = string.Join(".", p, 5, 4);
        }
        foreach (var kv in Column(v, LldpRemSysName).Concat(Column(v, LldpRemChassisId).Where(c => !v.ContainsKey(LldpRemSysName + "." + c.Key))).Take(1024))
        {
            var p = kv.Key.Split('.');
            if (p.Length < 3) continue;
            string? Col(string c) => Get(v, c + "." + kv.Key);
            localPort.TryGetValue(p[1], out var lp);
            var nb = new NetNeighbor
            {
                Protocol = "lldp", LocalPort = lp ?? p[1], RemoteName = Clean(Col(LldpRemSysName)), RemotePort = Clean(Col(LldpRemPortDesc)) ?? Clean(Col(LldpRemPortId)),
                RemoteDescription = Clean(Col(LldpRemSysDesc)), RemoteChassisId = ChassisId(Col(LldpRemChassisIdSubtype), Col(LldpRemChassisId)),
                RemoteAddress = manAddr.TryGetValue(kv.Key, out var ip) ? ip : null,
                Capabilities = LldpCapabilities(Col(LldpRemSysCapEnabled)),
            };
            nb.LocalIfIndex = n.Interfaces.FirstOrDefault(i => lp is not null && (string.Equals(i.Name, lp, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Description, lp, StringComparison.OrdinalIgnoreCase)))?.Index
                              ?? (int.TryParse(p[1], out var lpn) && byIndex.ContainsKey(lpn) ? lpn : (int?)null);
            if (nb.LocalIfIndex is { } li && byIndex.TryGetValue(li, out var lif)) nb.LocalPort = lif.Name ?? nb.LocalPort;
            n.Neighbors.Add(nb);
        }
        foreach (var kv in Column(v, CdpCacheDeviceId).Take(1024))
        {
            var p = kv.Key.Split('.');
            if (p.Length != 2 || !int.TryParse(p[0], out var ifi)) continue;
            string? Col(string c) => Get(v, c + "." + kv.Key);
            var name = Clean(kv.Value);
            // Same neighbour already known over LLDP on this port: CDP adds the platform.
            var existing = n.Neighbors.FirstOrDefault(x => x.LocalIfIndex == ifi && x.RemoteName is not null && name is not null && x.RemoteName.Split('.')[0].Equals(name.Split('.')[0], StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { existing.RemotePlatform ??= Clean(Col(CdpCachePlatform)); continue; }
            n.Neighbors.Add(new NetNeighbor
            {
                Protocol = "cdp", LocalIfIndex = byIndex.ContainsKey(ifi) ? ifi : (int?)null, LocalPort = byIndex.TryGetValue(ifi, out var it) ? it.Name : p[0],
                RemoteName = name, RemotePort = Clean(Col(CdpCacheDevicePort)), RemotePlatform = Clean(Col(CdpCachePlatform)),
                RemoteAddress = HexIp(Col(CdpCacheAddress)), Capabilities = CdpCapabilities(Col(CdpCacheCapabilities)),
            });
        }

        // ---- chassis, modules, power, CPU, routing, STP -------------------------------------------------------
        foreach (var kv in Column(v, SnmpMapper.EntPhysicalClass).Take(2048))
        {
            var cls = kv.Value switch { "3" => "chassis", "9" => "module", "6" => "powerSupply", "7" => "fan", "11" => "stack", "12" => "cpu", _ => null };
            if (cls is null) continue;
            string? Col(string c) => Get(v, c + "." + kv.Key);
            n.Modules.Add(new NetModule
            {
                Class = cls, Name = Clean(Col(EntPhysicalName)) ?? Clean(Col(EntPhysicalDescr)), Model = WindowsCollector.Clean(Col(SnmpMapper.EntPhysicalModelName)),
                Serial = WindowsCollector.Clean(Col(SnmpMapper.EntPhysicalSerialNum)), Firmware = Clean(Col(SnmpMapper.EntPhysicalSoftwareRev)) ?? Clean(Col(SnmpMapper.EntPhysicalFirmwareRev)),
            });
        }
        var chassis = n.Modules.Count(m => m.Class == "chassis" && m.Serial is not null);
        if (chassis > 0) n.StackMembers = chassis;

        var power = Column(v, PethMainPsePower).Select(x => Long(x.Value) ?? 0).Sum();
        var used = Column(v, PethMainPseConsumption).Select(x => Long(x.Value) ?? 0).Sum();
        if (power > 0) { n.PoeBudgetWatts = (int)power; n.PoeUsedWatts = (int)used; }
        foreach (var kv in Column(v, PethPsePortDetectionStatus))
        {
            var p = kv.Key.Split('.');
            if (p.Length != 2 || !int.TryParse(p[1], out var port)) continue;
            var ifi = IfOfPort(port);
            if (ifi is { } x && byIndex.TryGetValue(x, out var it)) it.PoeDelivering = kv.Value == "3";
        }

        var cpu = Column(v, CiscoCpu5Min).Select(x => Long(x.Value)).Where(x => x is not null).ToList();
        if (cpu.Count == 0) cpu = Column(v, HrProcessorLoad).Select(x => Long(x.Value)).Where(x => x is not null).ToList();
        if (cpu.Count > 0) n.CpuPercent = (int)Math.Round(cpu.Average(x => x!.Value));

        n.IpForwarding = Get(v, SnmpMapper.IpForwarding) is { } fw ? fw == "1" : (bool?)null;
        n.Routes = Long(Get(v, InetCidrRouteNumber)) ?? Long(Get(v, IpCidrRouteNumber));
        n.BridgeAddress = WindowsCollector.NormalizeMac(Get(v, SnmpMapper.Dot1dBaseBridgeAddress));
        var root = Get(v, Dot1dStpDesignatedRoot)?.Replace(" ", "").Replace(":", "");
        if (root is { Length: 16 } && n.BridgeAddress is not null) n.StpRoot = string.Equals(root.Substring(4), n.BridgeAddress.Replace(":", ""), StringComparison.OrdinalIgnoreCase);
        MapHealth(v, n, byIndex, IfOfPort);
        return n;
    }

    /// <summary>Port traffic counters, spanning-tree state, aggregation; memory, sensors, fans, power supplies; BGP / OSPF peers.</summary>
    private static void MapHealth(IReadOnlyDictionary<string, string> v, NetworkDeviceInfo n, Dictionary<int, NetInterface> byIndex, Func<int, int?> ifOfPort)
    {
        foreach (var kv in Column(v, Dot1dStpPortState))
            if (int.TryParse(kv.Key, out var bp) && ifOfPort(bp) is { } ifi && byIndex.TryGetValue(ifi, out var it))
                it.StpState = kv.Value switch { "1" => "disabled", "2" => "blocking", "3" => "listening", "4" => "learning", "5" => "forwarding", "6" => "broken", _ => null };
        foreach (var kv in Column(v, Dot3adAggPortAttachedAggId))
            if (int.TryParse(kv.Key, out var ifi) && int.TryParse(kv.Value, out var agg) && agg > 0 && agg != ifi && byIndex.TryGetValue(ifi, out var it)) it.LagIfIndex = agg;

        // Memory: RAM storage entries, else Cisco memory pools.
        long total = 0, used = 0;
        foreach (var kv in Column(v, HrStorageType))
        {
            if (kv.Value.TrimStart('.') != HrStorageRam) continue;
            var units = Long(Get(v, HrStorageUnits + "." + kv.Key)) ?? 1;
            total += (Long(Get(v, HrStorageSize + "." + kv.Key)) ?? 0) * units;
            used += (Long(Get(v, HrStorageUsed + "." + kv.Key)) ?? 0) * units;
        }
        if (total == 0)
            foreach (var kv in Column(v, CiscoMemPoolUsed))
            {
                var u = Long(kv.Value) ?? 0;
                var f = Long(Get(v, CiscoMemPoolFree + "." + kv.Key)) ?? 0;
                used += u; total += u + f;
            }
        if (total > 0) { n.MemoryPercent = (int)Math.Round(used * 100.0 / total); n.MemoryTotalMb = total / (1024 * 1024); }

        // ENTITY-SENSOR-MIB: value × 10^(scale exponent) / 10^precision.
        foreach (var kv in Column(v, EntPhySensorType).Take(512))
        {
            var (type, unit) = kv.Value switch
            {
                "8" => ("temperature", "°C"), "3" or "4" => ("voltage", "V"), "5" => ("current", "A"), "6" => ("power", "W"),
                "10" => ("fan", "rpm"), "14" => ("optical", "dBm"), _ => ("other", (string?)null),
            };
            if (type == "other") continue;
            var raw = Long(Get(v, EntPhySensorValue + "." + kv.Key));
            if (raw is null) continue;
            var scale = (int)(Long(Get(v, EntPhySensorScale + "." + kv.Key)) ?? 9);   // 9 = units
            var precision = (int)(Long(Get(v, EntPhySensorPrecision + "." + kv.Key)) ?? 0);
            var value = raw.Value * Math.Pow(10, (scale - 9) * 3) / Math.Pow(10, precision);
            if (type == "optical" && precision == 0 && Math.Abs(value) > 100) value /= 10;           // some report tenths of dBm
            var status = Get(v, EntPhySensorOperStatus + "." + kv.Key) switch { "1" => "ok", "2" => "unavailable", "3" => "nonoperational", _ => null };
            n.Sensors.Add(new NetSensor
            {
                Name = Clean(Get(v, EntPhysicalName + "." + kv.Key)) ?? Clean(Get(v, EntPhysicalDescr + "." + kv.Key)) ?? "Sensor " + kv.Key,
                Type = type, Unit = unit, Value = Math.Round(value, 2), Status = status,
            });
        }
        // Cisco environmental monitor.
        static string? EnvState(string? s) => s switch { "1" => "normal", "2" => "warning", "3" => "critical", "4" => "shutdown", "5" => "notPresent", "6" => "notFunctioning", _ => null };
        foreach (var kv in Column(v, CiscoEnvFanState))
            n.Components.Add(new NetComponent { Kind = "fan", Name = Clean(Get(v, CiscoEnvFanDescr + "." + kv.Key)) ?? "Fan " + kv.Key, Status = EnvState(kv.Value) });
        foreach (var kv in Column(v, CiscoEnvSupplyState))
            n.Components.Add(new NetComponent { Kind = "powerSupply", Name = Clean(Get(v, CiscoEnvSupplyDescr + "." + kv.Key)) ?? "Power supply " + kv.Key, Status = EnvState(kv.Value) });
        foreach (var kv in Column(v, CiscoEnvTempState))
        {
            var name = Clean(Get(v, CiscoEnvTempDescr + "." + kv.Key)) ?? "Temperature " + kv.Key;
            n.Components.Add(new NetComponent { Kind = "temperature", Name = name, Status = EnvState(kv.Value) });
            if (Long(Get(v, CiscoEnvTempValue + "." + kv.Key)) is { } t && t is > 0 and < 150 && !n.Sensors.Any(x => x.Type == "temperature" && x.Name == name))
                n.Sensors.Add(new NetSensor { Name = name, Type = "temperature", Unit = "°C", Value = t, Status = "ok" });
        }
        var temps = n.Sensors.Where(x => x.Type == "temperature" && x.Value is > -40 and < 150).Select(x => x.Value!.Value).ToList();
        if (temps.Count > 0) n.TemperatureC = temps.Max();

        // Routing peers.
        foreach (var kv in Column(v, BgpPeerState).Take(512))
        {
            var st = kv.Value switch { "1" => "idle", "2" => "connect", "3" => "active", "4" => "openSent", "5" => "openConfirm", "6" => "established", _ => "unknown" };
            n.RoutingPeers.Add(new NetRoutingPeer { Protocol = "bgp", Address = kv.Key, RemoteAs = Long(Get(v, BgpPeerRemoteAs + "." + kv.Key)), State = st, Up = st == "established" });
        }
        foreach (var kv in Column(v, OspfNbrState).Take(512))
        {
            var p = kv.Key.Split('.');
            if (p.Length < 4) continue;
            var st = kv.Value switch { "1" => "down", "2" => "attempt", "3" => "init", "4" => "twoWay", "5" => "exchangeStart", "6" => "exchange", "7" => "loading", "8" => "full", _ => "unknown" };
            n.RoutingPeers.Add(new NetRoutingPeer { Protocol = "ospf", Address = string.Join(".", p, 0, 4), State = st, Up = st is "full" or "twoWay" });
        }

        // Transceivers: ENTITY entries that are optics (SFP, SFP+, QSFP, XFP, GBIC).
        foreach (var kv in Column(v, SnmpMapper.EntPhysicalClass).Take(2048))
        {
            if (kv.Value is not ("10" or "9")) continue;
            var descr = Get(v, EntPhysicalDescr + "." + kv.Key) ?? "";
            var model = Get(v, SnmpMapper.EntPhysicalModelName + "." + kv.Key) ?? "";
            var text = (descr + " " + model).ToUpperInvariant();
            if (!(text.Contains("SFP") || text.Contains("XFP") || text.Contains("GBIC") || text.Contains("TRANSCEIVER") || text.Contains("OPTIC"))) continue;
            var serial = WindowsCollector.Clean(Get(v, SnmpMapper.EntPhysicalSerialNum + "." + kv.Key));
            var name = Clean(Get(v, EntPhysicalName + "." + kv.Key)) ?? Clean(descr);
            var existing = n.Modules.FirstOrDefault(m => m.Class == "module" && m.Name == name && m.Serial == serial);
            if (existing is not null) { existing.Class = "transceiver"; continue; }
            n.Modules.Add(new NetModule { Class = "transceiver", Name = name, Model = WindowsCollector.Clean(model) ?? Clean(descr), Serial = serial });
        }
    }

    /// <summary>What the device itself says it is over LLDP (bridge, router, wlan-ap, phone …).</summary>
    public static List<string> LocalCapabilities(IReadOnlyDictionary<string, string> v) => LldpCapabilities(Get(v, LldpLocSysCapEnabled));

    private static string IfTypeName(string? t) => t switch
    {
        "6" or "62" or "69" or "117" => "ethernet",
        "71" => "wireless",
        "161" => "lag",
        "53" or "135" or "136" => "vlan",
        "24" => "loopback",
        "131" or "150" => "tunnel",
        null => "other",
        _ => "other",
    };

    private static List<string> LldpCapabilities(string? hex)
    {
        // LLDP bit order (MSB first): other, repeater, bridge, wlanAccessPoint, router, telephone, docsis, station.
        var list = new List<string>();
        if (string.IsNullOrEmpty(hex)) return list;
        var h = hex!.Replace(" ", "").Replace(":", "");
        if (h.Length < 2 || !int.TryParse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return list;
        string[] names = { "other", "repeater", "bridge", "wlan-ap", "router", "phone", "docsis", "station" };
        for (var i = 0; i < 8; i++) if ((b & (0x80 >> i)) != 0) list.Add(names[i]);
        return list;
    }

    private static List<string> CdpCapabilities(string? hex)
    {
        // cdpCacheCapabilities: 4 bytes, bit 0x01 router, 0x02 transparent bridge, 0x04 source-route bridge, 0x08 switch,
        // 0x10 host, 0x20 IGMP, 0x40 repeater, 0x80 phone (bits of the last byte), 0x100 remote, 0x200 CVTA, 0x400 two-port MAC relay.
        var list = new List<string>();
        if (string.IsNullOrEmpty(hex)) return list;
        var h = hex!.Replace(" ", "").Replace(":", "");
        if (!long.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits)) return list;
        if ((bits & 0x01) != 0) list.Add("router");
        if ((bits & 0x0A) != 0) list.Add("bridge");
        if ((bits & 0x10) != 0) list.Add("station");
        if ((bits & 0x80) != 0) list.Add("phone");
        return list;
    }

    private static string? HexIp(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var h = hex!.Replace(" ", "").Replace(":", "");
        if (h.Length != 8) return null;
        var b = Enumerable.Range(0, 4).Select(i => Convert.ToInt32(h.Substring(i * 2, 2), 16));
        return string.Join(".", b);
    }

    private static string? MacFromIndex(string[] p, int start)
    {
        var bytes = new string[6];
        for (var i = 0; i < 6; i++)
        {
            if (!int.TryParse(p[start + i], out var x) || x < 0 || x > 255) return null;
            bytes[i] = x.ToString("X2", CultureInfo.InvariantCulture);
        }
        return string.Join(":", bytes);
    }

    /// <summary>LLDP chassis id by subtype: 4 = MAC address; 5 = network address; 1, 2, 3, 6, 7 = names (text).</summary>
    private static string? ChassisId(string? subtype, string? hex)
    {
        if (hex is null) return null;
        return subtype switch
        {
            "4" => WindowsCollector.NormalizeMac(hex) ?? hex,
            "5" => hex.Replace(" ", "").Length == 10 ? HexIp(hex.Replace(" ", "").Substring(2)) ?? hex : hex,
            "1" or "2" or "3" or "6" or "7" => HexText(hex) ?? hex,
            _ => WindowsCollector.NormalizeMac(hex) ?? HexText(hex) ?? hex,
        };
    }

    /// <summary>A chassis id that is a name (LLDP subtype "local" or "interface name") arrives hex-encoded: decode it when it is text.</summary>
    private static string? HexText(string hex)
    {
        var h = hex.Replace(" ", "").Replace(":", "");
        if (h.Length < 2 || h.Length % 2 != 0 || !h.All(Uri.IsHexDigit)) return null;
        var chars = new char[h.Length / 2];
        for (var i = 0; i < chars.Length; i++)
        {
            var b = Convert.ToInt32(h.Substring(i * 2, 2), 16);
            if (b < 0x20 || b > 0x7e) return null;
            chars[i] = (char)b;
        }
        return new string(chars);
    }

    private static bool IsHexLike(string? s) => s is not null && s.Length >= 12 && s.All(c => Uri.IsHexDigit(c) || c == ' ' || c == ':');

    private static IEnumerable<KeyValuePair<string, string>> Column(IReadOnlyDictionary<string, string> v, string column)
    {
        var prefix = column + ".";
        foreach (var kv in v)
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) yield return new KeyValuePair<string, string>(kv.Key.Substring(prefix.Length), kv.Value);
    }

    private static string? Get(IReadOnlyDictionary<string, string> v, string oid) => v.TryGetValue(oid, out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    private static long? Long(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : (long?)null;
    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s!.Trim().Trim('\0');
        return t.Length == 0 ? null : t.Length > 200 ? t.Substring(0, 200) : t;
    }
}
