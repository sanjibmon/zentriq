using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Application.Licensing;
using Zentriq.Domain.Licensing;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Inventory;

namespace Zentriq.Tests;

/// <summary>Inventory persistence against a real PostgreSQL (skipped unless ZENTRIQ_TEST_PG is set).</summary>
public sealed class InventoryStoreTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    // EnsureCapacityAsync only uses the connection it is given.
    private static InventoryStore Store() => new(new LicensingService(null!, null!, null!, null!, null!));

    private static LicenseStatus Trial(int devices) => new(LicenseMode.Trial, "trial", null, 14, ["*"], new Dictionary<string, int> { ["devices"] = devices }, null, null);

    private static InventoryDocument Doc(string host, string source, string? uuid = null, string? serial = null, string? mac = null, params string[] software)
    {
        var d = new InventoryDocument { Source = source, CollectorVersion = "test" };
        d.Device.Hostname = host;
        d.Device.Manufacturer = "Dell Inc.";
        d.Device.Model = "OptiPlex 7010";
        d.Device.SmbiosUuid = uuid;
        d.Device.SerialNumber = serial;
        d.Device.Category = "endpoint";
        d.Os.Family = "windows";
        d.Os.Name = "Windows 11 Pro";
        d.Hardware.CpuName = "Intel Core i5";
        d.Hardware.CpuCores = 6;
        d.Hardware.MemoryBytes = 16L * 1024 * 1024 * 1024;
        d.Hardware.Disks.Add(new Disk { Model = "SSD", SizeBytes = 512_000_000_000 });
        if (mac is not null) d.Network.Add(new NetworkAdapter { Name = "Ethernet", Mac = mac, Ipv4 = ["10.9.0.5"] });
        foreach (var s in software) d.Software.Add(new SoftwareItem { Name = s, Version = "1.0", Publisher = "Vendor", Source = "registry" });
        return d;
    }

    [DbFact]
    public async Task Agent_and_agentless_records_of_one_device_merge_and_software_is_replaced()
    {
        await using var conn = new NpgsqlConnection(_db.AppConnection);
        await conn.OpenAsync();
        var store = Store();
        var agentId = Guid.NewGuid();

        var scan = await store.ApplyAsync(conn, Doc("PC-7", "winrm", uuid: "4c4c4544-0001", serial: "ABC1234", mac: "aa:bb:cc:00:00:07", "7-Zip", "Chrome"), new MatchHints(Ip: IPAddress.Parse("10.9.0.5")), Trial(30), default);
        Assert.True(scan.Created);
        var agent = await store.ApplyAsync(conn, Doc("pc-7", "agent", uuid: "4c4c4544-0001", serial: "ABC1234", mac: "aa:bb:cc:00:00:07", "7-Zip", "Firefox", "Office"), new MatchHints(AgentId: agentId), Trial(30), default);
        Assert.False(agent.Created);
        Assert.Equal(scan.AssetId, agent.AssetId);

        await using var cmd = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM zentriq.assets),
                   (SELECT string_agg(name, ',' ORDER BY name) FROM zentriq.software_inventory WHERE asset_id = @a),
                   (SELECT inventory_source FROM zentriq.assets WHERE id = @a),
                   (SELECT count(*) FROM zentriq.asset_identifiers WHERE asset_id = @a AND kind = 'agent_id'),
                   (SELECT ram_gb FROM zentriq.hardware_inventory WHERE asset_id = @a),
                   (SELECT document->'device'->>'hostname' FROM zentriq.asset_inventory_documents WHERE asset_id = @a)
            """, conn);
        cmd.Parameters.AddWithValue("a", scan.AssetId!.Value);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(1L, r.GetInt64(0));
        Assert.Equal("7-Zip,Firefox,Office", r.GetString(1));
        Assert.Equal("agent", r.GetString(2));
        Assert.Equal(1L, r.GetInt64(3));
        Assert.Equal(16m, r.GetDecimal(4));
        Assert.Equal("pc-7", r.GetString(5));
    }

    [DbFact]
    public async Task Trial_total_device_cap_is_enforced()
    {
        await using var conn = new NpgsqlConnection(_db.AppConnection);
        await conn.OpenAsync();
        var store = Store();
        for (var i = 0; i < 3; i++)
            Assert.NotNull((await store.ApplyAsync(conn, Doc($"cap-{i}", "agent", uuid: $"uuid-{i}"), new MatchHints(), Trial(3), default)).AssetId);
        var refused = await store.ApplyAsync(conn, Doc("cap-4", "agent", uuid: "uuid-4"), new MatchHints(), Trial(3), default);
        Assert.Null(refused.AssetId);
        Assert.Contains("limit", refused.Note, StringComparison.OrdinalIgnoreCase);
        // An existing device can still update its inventory at the cap.
        Assert.NotNull((await store.ApplyAsync(conn, Doc("cap-0", "agent", uuid: "uuid-0", serial: null, mac: null, "New App"), new MatchHints(), Trial(3), default)).AssetId);
    }

    [Fact]
    public void Nul_characters_are_removed_and_escaped_text_is_kept()
    {
        var d = Doc("PC-NUL", "agent", null, null, null, "Remote Support\0", "C:\\u0000 literal");
        d.Software[0].Publisher = "Vendor\0\0";
        var clean = InventoryStore.WithoutNulChars(d);
        Assert.Equal("Remote Support", clean.Software[0].Name);
        Assert.Equal("Vendor", clean.Software[0].Publisher);
        Assert.Equal("C:\\u0000 literal", clean.Software[1].Name); // a backslash followed by "u0000" is ordinary text
        var ok = Doc("PC-OK", "agent", null, null, null, "7-Zip");
        Assert.Same(ok, InventoryStore.WithoutNulChars(ok));
    }

    [DbFact]
    public async Task Software_with_a_nul_character_is_stored()
    {
        await using var conn = new NpgsqlConnection(_db.AppConnection);
        await conn.OpenAsync();
        var r = await Store().ApplyAsync(conn, Doc("PC-NUL", "agent", uuid: "uuid-nul", serial: null, mac: null, "Zentriq Remote Support\0"), new MatchHints(AgentId: Guid.NewGuid()), Trial(30), default);
        Assert.NotNull(r.AssetId);
    }
}
