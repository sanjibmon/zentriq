using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Application.Licensing;
using Zentriq.Domain.Licensing;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Inventory;
using Zentriq.Worker.Agentless;

namespace Zentriq.Tests;

/// <summary>Network inventory read over SNMP: ports, VLANs, MAC address table, LLDP/CDP neighbours, PoE, spanning tree.</summary>
public sealed class NetworkSnmpTests
{
    private static Dictionary<string, string> Base(string descr, string oid, string name) => new()
    {
        [SnmpMapper.SysDescr] = descr, [SnmpMapper.SysObjectId] = oid, [SnmpMapper.SysName] = name, [SnmpMapper.SysUpTime] = "360000",
    };

    private static void Port(Dictionary<string, string> v, int i, string name, string type = "6", string oper = "1", string admin = "1", long highSpeed = 1000, string? mac = null)
    {
        v[$"{SnmpMapper.IfDescr}.{i}"] = name; v[$"{NetworkSnmp.IfName}.{i}"] = name; v[$"{SnmpMapper.IfType}.{i}"] = type;
        v[$"{SnmpMapper.IfOperStatus}.{i}"] = oper; v[$"{NetworkSnmp.IfAdminStatus}.{i}"] = admin; v[$"{NetworkSnmp.IfHighSpeed}.{i}"] = highSpeed.ToString();
        v[$"{SnmpMapper.IfPhysAddress}.{i}"] = mac ?? $"00AABBCC00{i:X2}";
        v[$"{NetworkSnmp.Dot1dBasePortIfIndex}.{i}"] = i.ToString();     // bridge port n = ifIndex n
    }

    /// <summary>A 4-port access switch: Gi1/0/1 uplink (LLDP) carrying VLANs 10 and 20, Gi1/0/2 a PC, Gi1/0/3 an unmanaged switch, Gi1/0/4 disabled.</summary>
    public static Dictionary<string, string> AccessSwitch()
    {
        var v = Base("Cisco IOS Software, C2960X Software (C2960X-UNIVERSALK9-M), Version 15.2(7)E8", "1.3.6.1.4.1.9.1.1208", "SW-ACCESS-1.corp.local");
        v[SnmpMapper.SysServices] = "2";
        v[SnmpMapper.Dot1dBaseBridgeAddress] = "001A2B3C4D00";
        v[NetworkSnmp.Dot1dStpDesignatedRoot] = "8000001A2B3C4D00";               // priority 32768 + own bridge address: root
        Port(v, 1, "Gi1/0/1"); Port(v, 2, "Gi1/0/2", highSpeed: 100); Port(v, 3, "Gi1/0/3"); Port(v, 4, "Gi1/0/4", oper: "2", admin: "2");
        Port(v, 50, "Vlan10", type: "53");
        v[$"{NetworkSnmp.Dot3Duplex}.2"] = "2";                                     // half duplex
        v[$"{NetworkSnmp.IfInErrors}.2"] = "17";
        v[$"{SnmpMapper.IfAlias}.1"] = "Uplink to core";
        v[$"{NetworkSnmp.IpAdEntIfIndex}.10.0.10.2"] = "50";
        // VLANs (Q-BRIDGE) and access VLAN per bridge port.
        v[$"{NetworkSnmp.Dot1qVlanStaticName}.10"] = "USERS"; v[$"{NetworkSnmp.Dot1qVlanStaticName}.20"] = "VOICE";
        v[$"{NetworkSnmp.Dot1qPvid}.2"] = "10"; v[$"{NetworkSnmp.Dot1qPvid}.3"] = "10";
        // MAC address table: vlan.m1..m6 → bridge port.
        v[$"{NetworkSnmp.Dot1qTpFdbPort}.10.0.17.34.51.68.1"] = "1";                // 00:11:22:33:44:01 on the uplink, VLAN 10
        v[$"{NetworkSnmp.Dot1qTpFdbPort}.20.0.17.34.51.68.2"] = "1";                // … and VLAN 20: trunk
        v[$"{NetworkSnmp.Dot1qTpFdbPort}.10.16.0.0.0.0.1"] = "2";                   // PC
        v[$"{NetworkSnmp.Dot1qTpFdbPort}.10.16.0.0.0.0.2"] = "3";                   // three devices behind port 3
        v[$"{NetworkSnmp.Dot1qTpFdbPort}.10.16.0.0.0.0.3"] = "3";
        v[$"{NetworkSnmp.Dot1qTpFdbPort}.10.16.0.0.0.0.4"] = "3";
        // LLDP neighbour on local port 1 with management address 10.0.0.1, capabilities bridge + router (0x28).
        v[$"{NetworkSnmp.LldpLocPortId}.1"] = "Gi1/0/1";
        v[$"{NetworkSnmp.LldpRemSysName}.0.1.1"] = "CORE-1";
        v[$"{NetworkSnmp.LldpRemPortId}.0.1.1"] = "Te1/1/1";
        v[$"{NetworkSnmp.LldpRemChassisId}.0.1.1"] = "001122334400";
        v[$"{NetworkSnmp.LldpRemChassisIdSubtype}.0.1.1"] = "4";
        v[$"{NetworkSnmp.LldpRemSysCapEnabled}.0.1.1"] = "28";
        v[$"{NetworkSnmp.LldpRemManAddrIfSubtype}.0.1.1.1.4.10.0.0.1"] = "2";
        // CDP: the same neighbour (adds the platform) and an IP phone on port 2.
        v[$"{NetworkSnmp.CdpCacheDeviceId}.1.1"] = "CORE-1.corp.local";
        v[$"{NetworkSnmp.CdpCachePlatform}.1.1"] = "cisco C9300-48P";
        v[$"{NetworkSnmp.CdpCacheDeviceId}.2.5"] = "SEP001122334455";
        v[$"{NetworkSnmp.CdpCacheDevicePort}.2.5"] = "Port 1";
        v[$"{NetworkSnmp.CdpCachePlatform}.2.5"] = "Cisco IP Phone 8841";
        v[$"{NetworkSnmp.CdpCacheAddress}.2.5"] = "0A000A21";
        v[$"{NetworkSnmp.CdpCacheCapabilities}.2.5"] = "00000090";
        // ENTITY-MIB chassis, power supply; PoE budget; CPU; ARP.
        v[$"{SnmpMapper.EntPhysicalClass}.1001"] = "3"; v[$"{SnmpMapper.EntPhysicalSerialNum}.1001"] = "FOC2233X0AB"; v[$"{SnmpMapper.EntPhysicalModelName}.1001"] = "WS-C2960X-48FPD-L";
        v[$"{SnmpMapper.EntPhysicalSoftwareRev}.1001"] = "15.2(7)E8";
        v[$"{SnmpMapper.EntPhysicalClass}.1010"] = "6"; v[$"{NetworkSnmp.EntPhysicalName}.1010"] = "Switch 1 - Power Supply A";
        v[$"{NetworkSnmp.PethMainPsePower}.1"] = "740"; v[$"{NetworkSnmp.PethMainPseConsumption}.1"] = "86";
        v[$"{NetworkSnmp.PethPsePortDetectionStatus}.1.2"] = "3";
        v[$"{NetworkSnmp.CiscoCpu5Min}.1"] = "12";
        v[$"{NetworkSnmp.IpNetToMediaPhys}.50.10.0.10.77"] = "100000000001";
        return v;
    }

    [Fact]
    public void Switch_ports_vlans_mac_table_neighbours_poe_and_stp()
    {
        var d = SnmpMapper.Map(AccessSwitch(), "10.0.10.2", "1");
        Assert.Equal("switch", d.Device.Chassis);
        var n = d.NetworkDevice!;
        Assert.Equal(5, n.Interfaces.Count);
        var up = n.Interfaces.Single(i => i.Index == 1);
        Assert.Equal(("Gi1/0/1", "ethernet", "up", 1000L, "Uplink to core"), (up.Name, up.Type, up.Oper, up.SpeedMbps!.Value, up.Alias));
        Assert.True(up.Trunk);                                                       // MACs learned in two VLANs
        var pc = n.Interfaces.Single(i => i.Index == 2);
        Assert.Equal((10, "half", 17L, true), (pc.Vlan!.Value, pc.Duplex, pc.InErrors!.Value, pc.PoeDelivering!.Value));
        Assert.Equal(("down", "down"), (n.Interfaces.Single(i => i.Index == 4).Admin, n.Interfaces.Single(i => i.Index == 4).Oper));
        Assert.Equal("vlan", n.Interfaces.Single(i => i.Index == 50).Type);
        Assert.Equal(new[] { "10.0.10.2" }, n.Interfaces.Single(i => i.Index == 50).Ipv4);

        Assert.Equal(new[] { (10, "USERS"), (20, "VOICE") }, n.Vlans.Select(x => (x.Id, x.Name!)));
        Assert.Equal(6, n.MacTable.Count);
        Assert.Equal(3, n.MacTable.Count(m => m.IfIndex == 3));
        Assert.Contains(n.MacTable, m => m.Mac == "10:00:00:00:00:01" && m.Vlan == 10 && m.IfIndex == 2);
        Assert.Single(n.Arp);
        Assert.Equal(("10.0.10.77", "10:00:00:00:00:01", 50), (n.Arp[0].Ip, n.Arp[0].Mac, n.Arp[0].IfIndex!.Value));

        Assert.Equal(2, n.Neighbors.Count);                                           // LLDP + CDP of the same device merged
        var core = n.Neighbors.Single(x => x.Protocol == "lldp");
        Assert.Equal(("Gi1/0/1", 1, "CORE-1", "Te1/1/1", "10.0.0.1", "cisco C9300-48P"), (core.LocalPort!, core.LocalIfIndex!.Value, core.RemoteName!, core.RemotePort!, core.RemoteAddress!, core.RemotePlatform!));
        Assert.Equal("00:11:22:33:44:00", core.RemoteChassisId);
        Assert.Equal(new[] { "bridge", "router" }, core.Capabilities);
        var phone = n.Neighbors.Single(x => x.Protocol == "cdp");
        Assert.Equal(("Gi1/0/2", "10.0.10.33", "Cisco IP Phone 8841"), (phone.LocalPort!, phone.RemoteAddress!, phone.RemotePlatform!));
        Assert.Contains("phone", phone.Capabilities);

        Assert.Equal((740, 86), (n.PoeBudgetWatts!.Value, n.PoeUsedWatts!.Value));
        Assert.Equal(12, n.CpuPercent);
        Assert.True(n.StpRoot);
        Assert.Equal(1, n.StackMembers);
        Assert.Contains(n.Modules, m => m.Class == "powerSupply" && m.Name == "Switch 1 - Power Supply A");
        Assert.Equal("00:1A:2B:3C:4D:00", n.BridgeAddress);
        Assert.True(d.Ports!.Switching);
    }

    [Fact]
    public void Access_points_routers_and_generic_devices_are_typed_from_their_interfaces_and_lldp()
    {
        // An access point bridges (BRIDGE-MIB) like a switch, but has radios and one wired port.
        var ap = Base("Linux AP version 6.5.62", "1.3.6.1.4.1.41112.1.6", "AP-LOBBY");
        ap[SnmpMapper.Dot1dBaseBridgeAddress] = "802AA8000001";
        Port(ap, 1, "eth0"); Port(ap, 2, "wifi0", type: "71"); Port(ap, 3, "wifi1", type: "71");
        Assert.Equal("access_point", SnmpMapper.Map(ap, "10.0.0.40", "1").Device.Chassis);

        // A device that says nothing useful but has a MAC address table is a switch.
        var generic = Base("Embedded device", "1.3.6.1.4.1.99999.1", "box-1");
        Port(generic, 1, "port1"); Port(generic, 2, "port2");
        generic[$"{NetworkSnmp.Dot1dTpFdbPort}.16.0.0.0.0.9"] = "2";
        Assert.Equal("switch", SnmpMapper.Map(generic, "10.0.0.41", "1").Device.Chassis);

        // LLDP self-capabilities: router only.
        var rt = Base("Embedded device", "1.3.6.1.4.1.99999.2", "edge-2");
        rt[NetworkSnmp.LldpLocSysCapEnabled] = "08";
        Assert.Equal("router", SnmpMapper.Map(rt, "10.0.0.42", "1").Device.Chassis);

        // Printers and computers carry no network-device inventory.
        var prn = Base("HP ETHERNET MULTI-ENVIRONMENT", "1.3.6.1.4.1.11.2.3.9.1", "PRN-1");
        prn[SnmpMapper.PrtGeneralSerial + ".1"] = "X";
        prn[SnmpMapper.Dot1dBaseBridgeAddress] = "001122334455";
        Assert.Null(SnmpMapper.Map(prn, "10.0.0.43", "1").NetworkDevice);
    }

    [Fact]
    public void Binary_octet_strings_arrive_as_hex_and_text_stays_text()
    {
        var values = new Dictionary<string, string>();
        SnmpCollector.Add(values, new[]
        {
            new Variable(new ObjectIdentifier(SnmpMapper.IfPhysAddress + ".1"), new OctetString(new byte[] { 0x00, 0x1a, 0x2b, 0x3c, 0x4d, 0x5e })),
            new Variable(new ObjectIdentifier(NetworkSnmp.LldpRemSysCapEnabled + ".0.1.1"), new OctetString(new byte[] { 0x28, 0x00 })),   // "(" would be printable
            new Variable(new ObjectIdentifier(SnmpMapper.IfDescr + ".1"), new OctetString("GigabitEthernet1/0/1")),
            new Variable(new ObjectIdentifier(NetworkSnmp.LldpRemChassisId + ".0.1.1"), new OctetString("core-1")),   // 6 characters: 12 hex digits, like a MAC
        });
        Assert.Equal("001A2B3C4D5E", values[SnmpMapper.IfPhysAddress + ".1"].ToUpperInvariant());
        Assert.StartsWith("28", values[NetworkSnmp.LldpRemSysCapEnabled + ".0.1.1"]);
        Assert.Equal("GigabitEthernet1/0/1", values[SnmpMapper.IfDescr + ".1"]);
        // A chassis id that is a name is decoded back to text.
        var v = Base("x", "1.3.6.1.4.1.99999.3", "sw");
        Port(v, 1, "p1");
        v[$"{NetworkSnmp.LldpRemSysName}.0.1.1"] = "core-1";
        v[$"{NetworkSnmp.LldpRemChassisId}.0.1.1"] = values[NetworkSnmp.LldpRemChassisId + ".0.1.1"];
        v[$"{NetworkSnmp.LldpRemChassisIdSubtype}.0.1.1"] = "7";                    // local (a name)
        Assert.Equal("core-1", NetworkSnmp.Map(v).Neighbors.Single().RemoteChassisId);
    }

    [Fact]
    public void Mac_addresses_are_normalised()
    {
        Assert.Equal("00:1a:2b:3c:4d:5e", NetworkStore.Mac("001A.2B3C.4D5E"));
        Assert.Equal("00:1a:2b:3c:4d:5e", NetworkStore.Mac("00-1A-2B-3C-4D-5E"));
        Assert.Null(NetworkStore.Mac("FF:FF:FF:FF:FF:FF"));
        Assert.Null(NetworkStore.Mac("00:00:00:00:00:00"));
        Assert.Null(NetworkStore.Mac("12345"));
    }
}

/// <summary>Stored network inventory: what is connected to each port, uplinks and unmanaged switches.</summary>
public sealed class NetworkStoreTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    private static InventoryStore Store() => new(new LicensingService(null!, null!, null!, null!, null!));
    private static LicenseStatus Trial() => new(LicenseMode.Trial, "trial", null, 14, ["*"], new Dictionary<string, int> { ["devices"] = 100 }, null, null);

    private static async Task<T> Scalar<T>(NpgsqlConnection c, string sql, Guid id)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("id", id);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    [DbFact]
    public async Task Switch_inventory_links_devices_to_ports_and_finds_unmanaged_switches()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        // Devices already in the inventory: the PC on port 2, a printer behind the unmanaged switch on port 3, the core switch.
        var pc = Guid.NewGuid(); var printer = Guid.NewGuid(); var core = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand("""
            INSERT INTO zentriq.assets (id, hostname, primary_ip, mac_address, category, device_type, source) VALUES
              (@pc, 'PC-7', '10.0.10.77', '10:00:00:00:00:01', 'endpoint', 'desktop', 'agent'),
              (@pr, 'PRN-3', '10.0.10.60', '10:00:00:00:00:02', 'printer', 'printer', 'discovery'),
              (@core, 'CORE-1', '10.0.0.1', NULL, 'network_device', 'switch', 'discovery')
            """, c))
        {
            ins.Parameters.AddWithValue("pc", pc); ins.Parameters.AddWithValue("pr", printer); ins.Parameters.AddWithValue("core", core);
            await ins.ExecuteNonQueryAsync();
        }

        var doc = SnmpMapper.Map(NetworkSnmpTests.AccessSwitch(), "10.0.10.2", "1");
        var r = await Store().ApplyAsync(c, doc, new MatchHints(Ip: System.Net.IPAddress.Parse("10.0.10.2")), Trial(), default);
        var sw = r.AssetId!.Value;

        Assert.Equal(5, await Scalar<long>(c, "SELECT count(*) FROM zentriq.network_interfaces WHERE asset_id = @id", sw));
        Assert.Equal(6, await Scalar<long>(c, "SELECT count(*) FROM zentriq.network_mac_table WHERE asset_id = @id", sw));
        Assert.Equal(4, await Scalar<int>(c, "SELECT ports_total FROM zentriq.network_devices WHERE asset_id = @id", sw));
        // The MAC address table is not kept in the document.
        Assert.False(await Scalar<bool>(c, "SELECT document->'networkDevice' ? 'macTable' FROM zentriq.asset_inventory_documents WHERE asset_id = @id", sw));

        // Port 1: LLDP neighbour CORE-1 (matched by management address) → uplink.
        Assert.Equal(core, await Scalar<Guid>(c, "SELECT neighbor_asset_id FROM zentriq.network_interfaces WHERE asset_id = @id AND if_index = 1", sw));
        Assert.True(await Scalar<bool>(c, "SELECT uplink FROM zentriq.network_interfaces WHERE asset_id = @id AND if_index = 1", sw));
        // Port 2: the PC (plus a CDP phone neighbour).
        Assert.Contains("PC-7", await Scalar<string>(c, "SELECT connected::text FROM zentriq.network_interfaces WHERE asset_id = @id AND if_index = 2", sw));
        Assert.False(await Scalar<bool>(c, "SELECT unmanaged_suspect FROM zentriq.network_interfaces WHERE asset_id = @id AND if_index = 2", sw));
        // Port 3: three devices, no neighbour, not a trunk → unmanaged switch; the printer is one of them.
        Assert.True(await Scalar<bool>(c, "SELECT unmanaged_suspect FROM zentriq.network_interfaces WHERE asset_id = @id AND if_index = 3", sw));
        Assert.Equal(3, await Scalar<int>(c, "SELECT mac_count FROM zentriq.network_interfaces WHERE asset_id = @id AND if_index = 3", sw));
        Assert.Contains("PRN-3", await Scalar<string>(c, "SELECT connected::text FROM zentriq.network_interfaces WHERE asset_id = @id AND if_index = 3", sw));

        // A full refresh (as after a scan) gives the same result; collecting again replaces the rows.
        await NetworkStore.RefreshAsync(c, null, default);
        Assert.True(await Scalar<bool>(c, "SELECT unmanaged_suspect FROM zentriq.network_interfaces WHERE asset_id = @id AND if_index = 3", sw));
        await Store().ApplyAsync(c, SnmpMapper.Map(NetworkSnmpTests.AccessSwitch(), "10.0.10.2", "1"), new MatchHints(PreferredAssetId: sw), Trial(), default);
        Assert.Equal(6, await Scalar<long>(c, "SELECT count(*) FROM zentriq.network_mac_table WHERE asset_id = @id", sw));
        Assert.Equal(2, await Scalar<long>(c, "SELECT count(*) FROM zentriq.network_neighbors WHERE asset_id = @id", sw));
    }
}

public sealed class SnmpSimFactAttribute : FactAttribute
{
    public SnmpSimFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZENTRIQ_TEST_SNMPSIM")))
            Skip = "ZENTRIQ_TEST_SNMPSIM not set (live SNMP test against snmpsim).";
    }
}

/// <summary>The real SNMP collector (GET, GETBULK walks, hex octet strings) against a simulated switch (snmpsim).</summary>
public sealed class SnmpLiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zq-snmpsim-" + Guid.NewGuid().ToString("N")[..8]);
    private System.Diagnostics.Process? _sim;
    private const int Port = 11161;

    public void Dispose()
    {
        try { if (_sim is { HasExited: false }) _sim.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _sim?.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>snmpsim record file (OID|type|value, in OID order): 2 integer, 4 text, 4x hex octets, 6 OID, 65 counter, 66 gauge, 67 timeticks.</summary>
    public static string SnmpRec(Dictionary<string, string> v)
    {
        static int[] Parts(string oid) => oid.Split('.').Select(int.Parse).ToArray();
        var hexPrefixes = NetworkSnmp.HexColumns;
        var lines = new List<(int[] Key, string Line)>();
        foreach (var (oid, value) in v)
        {
            string type, val = value;
            if (hexPrefixes.Any(h => oid == h || oid.StartsWith(h + ".", StringComparison.Ordinal))) { type = "4x"; val = value.Replace(" ", "").Replace(":", "").ToLowerInvariant(); }
            else if (oid == SnmpMapper.SysObjectId) type = "6";
            else if (oid == SnmpMapper.SysUpTime) type = "67";
            else if (oid.StartsWith(NetworkSnmp.IfInErrors + ".", StringComparison.Ordinal) || oid.StartsWith(NetworkSnmp.IfOutErrors + ".", StringComparison.Ordinal)) type = "65";
            else if (oid.StartsWith(NetworkSnmp.IfHighSpeed + ".", StringComparison.Ordinal)) type = "66";
            else if (long.TryParse(value, out var n) && n is >= int.MinValue and <= int.MaxValue && !value.StartsWith('0') || value == "0") type = "2";
            else type = "4";
            lines.Add((Parts(oid), $"{oid}|{type}|{val}"));
        }
        lines.Sort((a, b) =>
        {
            for (var i = 0; i < Math.Min(a.Key.Length, b.Key.Length); i++) if (a.Key[i] != b.Key[i]) return a.Key[i].CompareTo(b.Key[i]);
            return a.Key.Length.CompareTo(b.Key.Length);
        });
        return string.Join("\n", lines.Select(l => l.Line)) + "\n";
    }

    [SnmpSimFact]
    public async Task Collector_reads_a_switch_over_snmp_v2c()
    {
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "public.snmprec"), SnmpRec(NetworkSnmpTests.AccessSwitch()));
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("ZENTRIQ_TEST_SNMPSIM")!)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--data-dir=" + data);
        psi.ArgumentList.Add("--cache-dir=" + Path.Combine(_dir, "cache"));
        psi.ArgumentList.Add($"--agent-udpv4-endpoint=127.0.0.1:{Port}");
        _sim = System.Diagnostics.Process.Start(psi)!;
        var log = new System.Text.StringBuilder();
        _sim.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        _sim.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        _sim.BeginOutputReadLine(); _sim.BeginErrorReadLine();

        var cred = new ScanCredential(Guid.NewGuid(), "sim", "snmp_v2c", null, new Zentriq.Infrastructure.Secrets.CredentialSecret { Community = "public" },
            System.Text.Json.JsonDocument.Parse($$"""{"port":{{Port}}}""").RootElement, []);
        InventoryDocument? doc = null;
        for (var i = 0; i < 30 && doc is null; i++)
        {
            Assert.False(_sim.HasExited, "snmpsim exited: " + log);
            doc = await Task.Run(() => SnmpCollector.Collect(System.Net.IPAddress.Loopback, cred, TimeSpan.FromSeconds(2), "1"));
            if (doc is null) await Task.Delay(1000);
        }
        Assert.True(doc is not null, "No SNMP answer from snmpsim: " + log);
        Assert.Equal("switch", doc!.Device.Chassis);
        var n = doc.NetworkDevice!;
        Assert.Equal("v2c", n.SnmpVersion);
        Assert.Equal(5, n.Interfaces.Count);
        Assert.Equal("00:AA:BB:CC:00:01", n.Interfaces.Single(x => x.Index == 1).Mac);
        Assert.Equal(6, n.MacTable.Count);
        Assert.Equal(new[] { "USERS", "VOICE" }, n.Vlans.Select(x => x.Name));
        var core = n.Neighbors.Single(x => x.Protocol == "lldp");
        Assert.Equal(("CORE-1", "10.0.0.1", "00:11:22:33:44:00"), (core.RemoteName, core.RemoteAddress, core.RemoteChassisId));
        Assert.Equal(new[] { "bridge", "router" }, core.Capabilities);
        Assert.Equal("10.0.10.33", n.Neighbors.Single(x => x.Protocol == "cdp").RemoteAddress);
        Assert.True(n.StpRoot);
        Assert.Equal(740, n.PoeBudgetWatts);
        Assert.Equal("10:00:00:00:00:01", n.Arp.Single().Mac);
    }
}
