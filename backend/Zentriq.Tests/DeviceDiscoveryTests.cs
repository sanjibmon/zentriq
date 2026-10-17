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

/// <summary>Recognising devices that cannot run an agent: printers, scanners, switches, routers, firewalls and more.</summary>
public sealed class DeviceRecognitionTests
{
    [Theory]
    [InlineData("HP LaserJet Pro M404dn", "printer", "HP")]
    [InlineData("HP Color LaserJet MFP M480f", "mfp", "HP")]
    [InlineData("Canon imageRUNNER ADVANCE DX C3826", "mfp", "Canon")]
    [InlineData("KYOCERA TASKalfa 3253ci", "mfp", "Kyocera")]
    [InlineData("EPSON WF-C5790 Series", "printer", "Epson")]
    [InlineData("fi-7160 Network Scanner", "scanner", "Fujitsu / Ricoh")]
    [InlineData("FortiGate-60F | FortiGate", "firewall", "Fortinet")]
    [InlineData("pfSense - Login", "firewall", "Netgate")]
    [InlineData("SonicWall - Authentication", "firewall", "SonicWall")]
    [InlineData("Sophos Firewall", "firewall", "Sophos")]
    [InlineData("RouterOS router configuration page", "router", "MikroTik")]
    [InlineData("HPE Aruba ProCurve 2930F switch", "switch", "HPE Aruba")]
    [InlineData("Cisco CBS350-24P web management", "switch", "Cisco")]
    [InlineData("UniFi AP-AC-Pro access point", "access_point", null)]
    [InlineData("Synology DiskStation", "storage", "Synology")]
    [InlineData("Hikvision Network Video Recorder", "ip_camera", "Hikvision")]
    [InlineData("Yealink T46U", "ip_phone", "Yealink")]
    [InlineData("APC Network Management Card", "ups", "APC (Schneider Electric)")]
    [InlineData("VMware ESXi", "hypervisor", "VMware")]
    [InlineData("BIG-IP Configuration Utility", "load_balancer", "F5")]
    public void Web_interface_text_identifies_the_device(string text, string type, string? maker)
    {
        var fp = DeviceFingerprint.FromText(text, "web interface")!;
        Assert.Equal(type, fp.DeviceType);
        if (maker is not null) Assert.Equal(maker, fp.Manufacturer);
        Assert.NotEmpty(fp.Evidence);
        Assert.NotNull(DeviceTypes.Get(type));
    }

    [Fact]
    public void Ordinary_pages_are_not_mistaken_for_devices()
    {
        Assert.Null(DeviceFingerprint.FromText("Welcome to nginx!", "web"));
        Assert.Null(DeviceFingerprint.FromText("IIS Windows Server", "web"));
        Assert.Null(DeviceFingerprint.FromText(null, "web"));
    }

    [Fact]
    public void Ports_give_a_first_guess()
    {
        Assert.Equal("printer", DeviceFingerprint.FromPorts([80, 443, 9100])!.DeviceType);
        Assert.Equal("router", DeviceFingerprint.FromPorts([22, 80, 8291])!.DeviceType);
        Assert.Equal("ip_camera", DeviceFingerprint.FromPorts([80, 554])!.DeviceType);
        Assert.Equal("hypervisor", DeviceFingerprint.FromPorts([443, 902])!.DeviceType);
        Assert.Null(DeviceFingerprint.FromPorts([135, 445, 3389]));    // a Windows computer: the agent or a credential tells more
        Assert.Null(DeviceFingerprint.FromPorts([22]));
    }

    [Fact]
    public void Web_evidence_beats_port_evidence_and_model_comes_from_the_title()
    {
        var web = WebFingerprinter.Recognise(new WebFingerprinter.WebInfo("https://10.0.0.9/", "FortiGate-60F - Login", "xxxxxxxx-xxxxx", null));
        var combined = DeviceFingerprint.Combine(web, DeviceFingerprint.FromPorts([443, 23]));
        Assert.Equal("firewall", combined.DeviceType);
        Assert.Equal("Fortinet", combined.Manufacturer);
        Assert.Equal("FortiGate-60F", combined.Model);
        Assert.Null(DeviceFingerprint.ModelFromTitle("Login"));
        Assert.Equal("HP LaserJet Pro M404dn", WebFingerprinter.TitleOf("<html><head><TITLE>\n HP LaserJet Pro M404dn&nbsp;</TITLE></head>")?.Replace(' ', ' ').Trim());
    }

    private static Dictionary<string, string> Base(string descr, string oid, string name) => new()
    {
        [SnmpMapper.SysDescr] = descr, [SnmpMapper.SysObjectId] = oid, [SnmpMapper.SysName] = name, [SnmpMapper.SysUpTime] = "1000",
    };

    [Fact]
    public void Snmp_switch_router_and_firewall()
    {
        var sw = Base("Cisco IOS Software, C2960X Software", "1.3.6.1.4.1.9.1.1208", "sw-1");
        sw[SnmpMapper.Dot1dBaseBridgeAddress] = "00 1a 2b 3c 4d 5e";
        sw[SnmpMapper.SysServices] = "2";
        sw[SnmpMapper.IfType + ".1"] = "6"; sw[SnmpMapper.IfOperStatus + ".1"] = "1"; sw[SnmpMapper.IfDescr + ".1"] = "Gi1/0/1"; sw[SnmpMapper.IfPhysAddress + ".1"] = "001a2b3c4d01";
        sw[SnmpMapper.IfType + ".2"] = "6"; sw[SnmpMapper.IfOperStatus + ".2"] = "2"; sw[SnmpMapper.IfDescr + ".2"] = "Gi1/0/2"; sw[SnmpMapper.IfPhysAddress + ".2"] = "001a2b3c4d02";
        sw[SnmpMapper.IfAlias + ".1"] = "Uplink to core";
        var d = SnmpMapper.Map(sw, "10.0.0.2", "1");
        Assert.Equal("switch", d.Device.Chassis);
        Assert.Equal("network_device", d.Device.Category);
        Assert.Equal(2, d.Ports!.Physical);
        Assert.Equal(1, d.Ports.Up);
        Assert.Equal("up", d.Network[0].Status);
        Assert.Equal("Uplink to core", d.Network[0].Alias);

        var rt = Base("Juniper Networks, Inc. mx204", "1.3.6.1.4.1.2636.1.1.1.2.137", "edge-1");
        rt[SnmpMapper.IpForwarding] = "1";
        rt[SnmpMapper.SysServices] = "4";
        Assert.Equal("router", SnmpMapper.Map(rt, "10.0.0.1", "1").Device.Chassis);

        var fw = Base("FortiGate-100F v7.2.8,build1639", "1.3.6.1.4.1.12356.101.1.1", "fw-1");
        fw[SnmpMapper.IpForwarding] = "1";
        var f = SnmpMapper.Map(fw, "10.0.0.254", "1");
        Assert.Equal("firewall", f.Device.Chassis);
        Assert.Equal("Fortinet", f.Device.Manufacturer);
        Assert.NotNull(f.Device.IdentifiedBy);
    }

    [Fact]
    public void Snmp_printer_page_count_supplies_and_model()
    {
        var p = Base("HP ETHERNET MULTI-ENVIRONMENT,ROM none,JETDIRECT", "1.3.6.1.4.1.11.2.3.9.1", "PRN-FLOOR2");
        p[SnmpMapper.PrtGeneralSerial + ".1"] = "PHB8K01234";
        p[SnmpMapper.PrtMarkerLifeCount + ".1.1"] = "48213";
        p[SnmpMapper.HrPrinterStatus + ".1"] = "3";
        p[SnmpMapper.HrDeviceDescr + ".1"] = "HP Color LaserJet MFP M480f";
        p[SnmpMapper.PrtSuppliesDescription + ".1.1"] = "Black Cartridge HP W2030A";
        p[SnmpMapper.PrtSuppliesMaxCapacity + ".1.1"] = "100";
        p[SnmpMapper.PrtSuppliesLevel + ".1.1"] = "12";
        p[SnmpMapper.PrtSuppliesDescription + ".1.2"] = "Cyan Cartridge HP W2031A";
        p[SnmpMapper.PrtSuppliesMaxCapacity + ".1.2"] = "100";
        p[SnmpMapper.PrtSuppliesLevel + ".1.2"] = "64";
        var d = SnmpMapper.Map(p, "10.0.0.50", "1");
        Assert.Equal("printer", d.Device.Category);
        Assert.Equal("printer", d.Device.Chassis);        // the network card description says JetDirect: a printer
        Assert.Equal("HP Color LaserJet MFP M480f", d.Device.Model);
        Assert.Equal("PHB8K01234", d.Device.SerialNumber);
        Assert.Equal(48213, d.Printer!.PageCount);
        Assert.Equal("idle", d.Printer.Status);
        Assert.Equal(12, d.Printer.Supplies.Single(s => s.Name.StartsWith("Black")).LevelPercent);
        Assert.Null(d.Ports);
    }

    [Fact]
    public void Device_types_map_to_categories()
    {
        Assert.Equal("network_device", DeviceTypes.CategoryFor("firewall"));
        Assert.Equal("printer", DeviceTypes.CategoryFor("scanner"));
        Assert.Equal("iot", DeviceTypes.CategoryFor("ip_camera"));
        Assert.Null(DeviceTypes.CategoryFor("virtual"));
        Assert.False(DeviceTypes.IsKnown("toaster"));
        Assert.Equal("laptop", InventoryStore.DeviceTypeOf(new InventoryDocument { Device = { Chassis = "laptop", Category = "endpoint" } }));
        Assert.Equal("desktop", InventoryStore.DeviceTypeOf(new InventoryDocument { Device = { Category = "endpoint" } }));
        Assert.Equal("virtual", InventoryStore.DeviceTypeOf(new InventoryDocument { Device = { Category = "server" }, Hardware = { IsVirtual = true } }));
    }

    [Fact]
    public void The_record_kept_is_the_one_with_inventory_then_the_newest()
    {
        var now = DateTimeOffset.UtcNow;
        DuplicateAssets.Member M(string id, string inv, int ageMin, bool agent = false) =>
            new(Guid.Parse(id), "PC-1", null, null, null, "discovery", inv, now.AddMinutes(-ageMin), now.AddDays(-5), null, agent);
        var scanned = M("00000000-0000-0000-0000-000000000001", "not_collected", 1);
        var inventoried = M("00000000-0000-0000-0000-000000000002", "success", 60, agent: true);
        var older = M("00000000-0000-0000-0000-000000000003", "not_collected", 600);
        Assert.Equal(inventoried.Id, DuplicateAssets.ChooseKeep([scanned, inventoried, older]).Id);
        Assert.Equal(scanned.Id, DuplicateAssets.ChooseKeep([scanned, older]).Id);
    }
}

/// <summary>Duplicates, manual device types and deletion against a real PostgreSQL (skipped unless ZENTRIQ_TEST_PG is set).</summary>
public sealed class DeviceRecordTests : IAsyncLifetime
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

    private static async Task<Guid> Insert(NpgsqlConnection c, string? host, string? ip, string? serial, string? mac, int ageMinutes, string inv = "not_collected")
    {
        var id = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.assets (id, hostname, primary_ip, serial_number, mac_address, category, source, inventory_status, first_seen_at, last_seen_at)
            VALUES (@id, @h, @ip::inet, @sn, @mac::macaddr, 'endpoint', 'discovery', @inv, now() - interval '10 days', now() - make_interval(mins => @age))
            """, c);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("h", (object?)host ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ip", (object?)ip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sn", (object?)serial ?? DBNull.Value);
        cmd.Parameters.AddWithValue("mac", (object?)mac ?? DBNull.Value);
        cmd.Parameters.AddWithValue("inv", inv);
        cmd.Parameters.AddWithValue("age", ageMinutes);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<(string Status, Guid? MergedInto, string? Ip)> Row(NpgsqlConnection c, Guid id)
    {
        await using var cmd = new NpgsqlCommand("SELECT status, merged_into, host(primary_ip) FROM zentriq.assets WHERE id = @id", c);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    [DbFact]
    public async Task Duplicates_are_found_merged_into_one_record_with_the_newest_address()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var inventoried = await Insert(c, "PC-SALES-7", "10.0.0.20", "5CG1234XYZ", null, 120, "success");
        var scanned = await Insert(c, "pc-sales-7", "10.0.0.77", null, null, 1);                    // same name, newer address
        var bySerial = await Insert(c, "LAPTOP-OLD", "10.0.0.30", "5CG1234XYZ", null, 3000);        // same serial
        var byMac = await Insert(c, null, "10.0.0.31", null, "00:1a:2b:3c:4d:5e", 10);
        var byMac2 = await Insert(c, "PRN-2", "10.0.0.32", null, "00:1a:2b:3c:4d:5e", 5);
        var vpnA = await Insert(c, "PC-A", "10.0.1.1", null, "02:00:4c:4f:4f:50", 5);               // locally administered (Npcap loopback)
        var vpnB = await Insert(c, "PC-B", "10.0.1.2", null, "02:00:4c:4f:4f:50", 5);
        var twinA = await Insert(c, "KIOSK", "10.0.2.1", "SERIAL-AAAA1", null, 5);                  // same name, different serials
        var twinB = await Insert(c, "KIOSK", "10.0.2.2", "SERIAL-BBBB2", null, 5);

        var groups = await DuplicateAssets.FindAsync(c, default);
        Assert.Equal(2, groups.Count);
        var pc = groups.Single(g => g.Keep.Id == inventoried);
        Assert.Equal(new[] { scanned, bySerial }.Order(), pc.Merge.Select(m => m.Id).Order());
        Assert.Contains(pc.Reasons, r => r.StartsWith("same serial number", StringComparison.Ordinal));
        var mac = groups.Single(g => g.Keep.Id == byMac2 || g.Keep.Id == byMac);
        Assert.Equal(byMac2, mac.Keep.Id);                                                            // newest wins when none has inventory
        Assert.DoesNotContain(groups, g => new[] { vpnA, vpnB, twinA, twinB }.Contains(g.Keep.Id));

        Assert.Equal(2, await DuplicateAssets.MergeAsync(c, pc.Keep.Id, pc.Merge.Select(m => m.Id).ToList(), default));
        var kept = await Row(c, inventoried);
        Assert.Equal("active", kept.Status);
        Assert.Equal("10.0.0.77", kept.Ip);                                                           // newest address
        Assert.Equal(("duplicate", (Guid?)inventoried), ((await Row(c, scanned)).Status, (await Row(c, scanned)).MergedInto));
        Assert.Single(await DuplicateAssets.FindAsync(c, default));

        // Deleting the kept device removes the records merged into it too.
        Assert.Equal(3, await DuplicateAssets.DeleteAsync(c, [inventoried], default));
        await using var left = new NpgsqlCommand("SELECT count(*) FROM zentriq.assets WHERE id = ANY(@ids)", c);
        left.Parameters.AddWithValue("ids", new[] { inventoried, scanned, bySerial });
        Assert.Equal(0L, (long)(await left.ExecuteScalarAsync())!);
    }

    [DbFact]
    public async Task A_device_type_set_by_an_administrator_is_kept_by_inventory()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var doc = new InventoryDocument { Source = "snmp", Device = { Hostname = "PRN-9", Category = "printer", Chassis = "printer", SerialNumber = "CNB9X12345" } };
        doc.Network.Add(new NetworkAdapter { Name = "eth0", Mac = "00:11:22:33:44:55", Ipv4 = ["10.0.9.9"] });
        var r = await Store().ApplyAsync(c, doc, new MatchHints(Ip: System.Net.IPAddress.Parse("10.0.9.9")), Trial(), default);
        await using (var cmd = new NpgsqlCommand("SELECT device_type FROM zentriq.assets WHERE id = @id", c))
        {
            cmd.Parameters.AddWithValue("id", r.AssetId!.Value);
            Assert.Equal("printer", await cmd.ExecuteScalarAsync());
        }
        await using (var set = new NpgsqlCommand("UPDATE zentriq.assets SET device_type = 'mfp', device_type_manual = true WHERE id = @id", c))
        {
            set.Parameters.AddWithValue("id", r.AssetId!.Value);
            await set.ExecuteNonQueryAsync();
        }
        await Store().ApplyAsync(c, doc, new MatchHints(Ip: System.Net.IPAddress.Parse("10.0.9.9")), Trial(), default);
        await using var get = new NpgsqlCommand("SELECT device_type, device_type_manual FROM zentriq.assets WHERE id = @id", c);
        get.Parameters.AddWithValue("id", r.AssetId!.Value);
        await using var rd = await get.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync());
        Assert.Equal("mfp", rd.GetString(0));
        Assert.True(rd.GetBoolean(1));
    }
}
