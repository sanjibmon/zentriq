using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Infrastructure.Licensing;

namespace Zentriq.Tests;

/// <summary>Build 119: only reachable devices take a license; a device with two addresses is one device.</summary>
public sealed class Release119DbTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    private static async Task<Guid> Asset(NpgsqlConnection c, string? host, string ip, string inventory, string? unreachable = null)
    {
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO zentriq.assets (hostname, primary_ip, category, status, source, inventory_status, last_seen_at, unreachable_since)
            VALUES (@h, @ip::inet, 'endpoint', 'active', 'discovery', @inv, now() - interval '1 hour', {unreachable ?? "NULL"}) RETURNING id
            """, c);
        cmd.Parameters.AddWithValue("h", (object?)host ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ip", ip);
        cmd.Parameters.AddWithValue("inv", inventory);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task Exec(NpgsqlConnection c, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    [DbFact]
    public async Task Only_reachable_devices_and_one_record_per_device_take_a_license()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        await using var c = await ds.OpenConnectionAsync();
        var licensing = new LicensingService(null!, ds, null!, null!, null!);

        await Asset(c, "printer-1", "10.0.0.20", "not_collected");                                     // answered the last scan: counts
        await Asset(c, null, "10.0.0.21", "not_collected", "now()");                                     // did not answer since: no license
        var withAgent = await Asset(c, "pc-7", "10.0.0.22", "success", "now()");                         // has an agent: counts although unreachable
        await Exec(c, "INSERT INTO zentriq.agent_registrations (asset_id, agent_version, status, capabilities, platform, last_checkin_at) VALUES (@a, '1.1.1.119', 'active', '{}', 'windows-x64', now())", ("a", withAgent));
        var laptop = await Asset(c, "laptop-3", "10.0.0.30", "success");                               // inventoried, two adapters
        await Exec(c, """
            INSERT INTO zentriq.asset_inventory_documents (asset_id, source, collected_at, document)
            VALUES (@a, 'agent', now(), '{"network":[{"name":"Ethernet","ipv4":["10.0.0.30"]},{"name":"Wi-Fi","ipv4":["10.0.5.31"]}]}'::jsonb)
            """, ("a", laptop));
        var wifi = await Asset(c, null, "10.0.5.31", "not_collected");                                 // the laptop's Wi-Fi address, found by a scan

        var counts = await licensing.CountBillableAsync(c, null);
        Assert.Equal(3, counts.Values.Sum());   // printer-1, pc-7, laptop-3

        // It answers again: it counts again.
        await Exec(c, "UPDATE zentriq.assets SET last_seen_at = now() + interval '1 minute' WHERE primary_ip = '10.0.0.21'::inet");
        Assert.Equal(4, (await licensing.CountBillableAsync(c, null)).Values.Sum());

        // The Wi-Fi record is merged into the laptop (one device, both addresses).
        var groups = await DuplicateAssets.FindAsync(c, default);
        var g = Assert.Single(groups, x => x.Keep.Id == laptop);
        Assert.Contains(g.Merge, m => m.Id == wifi);
        Assert.Contains(g.Reasons, r => r.Contains("another adapter"));
    }
}
