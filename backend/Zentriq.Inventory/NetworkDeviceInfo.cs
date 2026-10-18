using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Zentriq.Inventory;

/// <summary>
/// Network inventory of a switch, router, firewall, access point or controller, read over SNMP from standard MIBs:
/// IF-MIB / EtherLike-MIB (ports), BRIDGE-MIB / Q-BRIDGE-MIB (MAC address table, VLANs), LLDP-MIB and CISCO-CDP-MIB
/// (neighbours), IP-MIB (ARP, forwarding, routes), ENTITY-MIB (stack members, modules, power supplies, fans),
/// POWER-ETHERNET-MIB (PoE), HOST-RESOURCES-MIB / CISCO-PROCESS-MIB (CPU) and BRIDGE-MIB spanning tree.
/// </summary>
public sealed class NetworkDeviceInfo
{
    [JsonPropertyName("interfaces")] public List<NetInterface> Interfaces { get; set; } = new();
    [JsonPropertyName("neighbors")] public List<NetNeighbor> Neighbors { get; set; } = new();
    [JsonPropertyName("vlans")] public List<NetVlan> Vlans { get; set; } = new();
    /// <summary>MAC address table (forwarding database): which device MAC was learned on which port.</summary>
    [JsonPropertyName("macTable")] public List<NetMacEntry> MacTable { get; set; } = new();
    /// <summary>ARP table (routers, layer-3 switches, firewalls): IP address of each MAC.</summary>
    [JsonPropertyName("arp")] public List<NetArpEntry> Arp { get; set; } = new();
    [JsonPropertyName("modules")] public List<NetModule> Modules { get; set; } = new();
    [JsonPropertyName("bridgeAddress")] public string? BridgeAddress { get; set; }
    [JsonPropertyName("ipForwarding")] public bool? IpForwarding { get; set; }
    [JsonPropertyName("routes")] public long? Routes { get; set; }
    [JsonPropertyName("cpuPercent")] public int? CpuPercent { get; set; }
    [JsonPropertyName("poeBudgetWatts")] public int? PoeBudgetWatts { get; set; }
    [JsonPropertyName("poeUsedWatts")] public int? PoeUsedWatts { get; set; }
    /// <summary>This device is the spanning-tree root bridge.</summary>
    [JsonPropertyName("stpRoot")] public bool? StpRoot { get; set; }
    /// <summary>Stack members / chassis (ENTITY-MIB class chassis).</summary>
    [JsonPropertyName("stackMembers")] public int? StackMembers { get; set; }
    [JsonPropertyName("snmpVersion")] public string? SnmpVersion { get; set; }
    /// <summary>Memory in use (HOST-RESOURCES-MIB RAM or CISCO-MEMORY-POOL-MIB).</summary>
    [JsonPropertyName("memoryPercent")] public int? MemoryPercent { get; set; }
    [JsonPropertyName("memoryTotalMb")] public long? MemoryTotalMb { get; set; }
    /// <summary>Highest temperature reported by a sensor (°C).</summary>
    [JsonPropertyName("temperatureC")] public double? TemperatureC { get; set; }
    /// <summary>ENTITY-SENSOR-MIB readings: temperature, voltage, current, power, fan speed, optical power (transceivers).</summary>
    [JsonPropertyName("sensors")] public List<NetSensor> Sensors { get; set; } = new();
    /// <summary>Fans and power supplies with their state (CISCO-ENVMON-MIB, ENTITY-STATE-MIB).</summary>
    [JsonPropertyName("components")] public List<NetComponent> Components { get; set; } = new();
    /// <summary>BGP and OSPF neighbours (routers, firewalls, layer-3 switches).</summary>
    [JsonPropertyName("routingPeers")] public List<NetRoutingPeer> RoutingPeers { get; set; } = new();
}

public sealed class NetSensor
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>temperature | voltage | current | power | fan | optical | other</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "other";
    [JsonPropertyName("value")] public double? Value { get; set; }
    /// <summary>°C, V, A, W, rpm, dBm</summary>
    [JsonPropertyName("unit")] public string? Unit { get; set; }
    /// <summary>ok | unavailable | nonoperational</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }
}

public sealed class NetComponent
{
    /// <summary>fan | powerSupply | temperature</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "fan";
    [JsonPropertyName("name")] public string? Name { get; set; }
    /// <summary>normal | warning | critical | shutdown | notPresent | notFunctioning</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }
}

public sealed class NetRoutingPeer
{
    /// <summary>bgp | ospf</summary>
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "bgp";
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("remoteAs")] public long? RemoteAs { get; set; }
    /// <summary>BGP: idle … established; OSPF: down … full.</summary>
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("up")] public bool Up { get; set; }
}

public sealed class NetInterface
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    /// <summary>ethernet | wireless | lag | vlan | loopback | tunnel | other (from ifType).</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    /// <summary>up | down | testing (ifAdminStatus).</summary>
    [JsonPropertyName("admin")] public string? Admin { get; set; }
    /// <summary>up | down | dormant | notPresent | lowerLayerDown (ifOperStatus).</summary>
    [JsonPropertyName("oper")] public string? Oper { get; set; }
    [JsonPropertyName("speedMbps")] public long? SpeedMbps { get; set; }
    /// <summary>full | half | unknown (EtherLike-MIB).</summary>
    [JsonPropertyName("duplex")] public string? Duplex { get; set; }
    [JsonPropertyName("mtu")] public int? Mtu { get; set; }
    /// <summary>Untagged / access VLAN (Q-BRIDGE pvid or Cisco vmVlan).</summary>
    [JsonPropertyName("vlan")] public int? Vlan { get; set; }
    /// <summary>The port carries several VLANs (more than one VLAN learned, or a trunk).</summary>
    [JsonPropertyName("trunk")] public bool? Trunk { get; set; }
    [JsonPropertyName("poe")] public bool? PoeDelivering { get; set; }
    [JsonPropertyName("inErrors")] public long? InErrors { get; set; }
    [JsonPropertyName("outErrors")] public long? OutErrors { get; set; }
    [JsonPropertyName("ipv4")] public List<string> Ipv4 { get; set; } = new();
    /// <summary>Seconds since boot when the port last changed state (ifLastChange).</summary>
    [JsonPropertyName("lastChangeSec")] public long? LastChangeSeconds { get; set; }
    /// <summary>64-bit octet counters (IF-MIB ifHCIn/OutOctets); traffic rates come from two collections.</summary>
    [JsonPropertyName("inOctets")] public long? InOctets { get; set; }
    [JsonPropertyName("outOctets")] public long? OutOctets { get; set; }
    [JsonPropertyName("inDiscards")] public long? InDiscards { get; set; }
    [JsonPropertyName("outDiscards")] public long? OutDiscards { get; set; }
    /// <summary>Spanning tree: forwarding | blocking | listening | learning | disabled | broken.</summary>
    [JsonPropertyName("stpState")] public string? StpState { get; set; }
    /// <summary>The link aggregation (port-channel) this port belongs to (ifIndex).</summary>
    [JsonPropertyName("lagIfIndex")] public int? LagIfIndex { get; set; }
}

public sealed class NetNeighbor
{
    /// <summary>lldp | cdp</summary>
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "lldp";
    [JsonPropertyName("localPort")] public string? LocalPort { get; set; }
    [JsonPropertyName("localIfIndex")] public int? LocalIfIndex { get; set; }
    [JsonPropertyName("remoteName")] public string? RemoteName { get; set; }
    [JsonPropertyName("remotePort")] public string? RemotePort { get; set; }
    [JsonPropertyName("remoteDescription")] public string? RemoteDescription { get; set; }
    [JsonPropertyName("remotePlatform")] public string? RemotePlatform { get; set; }
    [JsonPropertyName("remoteChassisId")] public string? RemoteChassisId { get; set; }
    [JsonPropertyName("remoteAddress")] public string? RemoteAddress { get; set; }
    /// <summary>bridge, router, wlan-ap, phone, station, other (LLDP / CDP capabilities).</summary>
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = new();
}

public sealed class NetVlan
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class NetMacEntry
{
    [JsonPropertyName("mac")] public string Mac { get; set; } = "";
    [JsonPropertyName("vlan")] public int? Vlan { get; set; }
    [JsonPropertyName("ifIndex")] public int? IfIndex { get; set; }
}

public sealed class NetArpEntry
{
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("mac")] public string Mac { get; set; } = "";
    [JsonPropertyName("ifIndex")] public int? IfIndex { get; set; }
}

public sealed class NetModule
{
    /// <summary>chassis | module | powerSupply | fan | stack | cpu | port | sensor | other (entPhysicalClass).</summary>
    [JsonPropertyName("class")] public string Class { get; set; } = "other";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("serial")] public string? Serial { get; set; }
    [JsonPropertyName("firmware")] public string? Firmware { get; set; }
}
