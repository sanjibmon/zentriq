using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;

namespace Zentriq.Tests;

/// <summary>zentriq.asset_ipv4: the IPv4 address shown for a device in the Agents and Remote Support lists.</summary>
public sealed class AssetIpv4Tests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    [DbFact]
    public async Task Picks_an_ipv4_address_of_the_device()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        async Task<string?> Ip(string primary, string? doc)
        {
            var id = Guid.NewGuid();
            await using (var ins = new NpgsqlCommand("INSERT INTO zentriq.assets (id, hostname, primary_ip, category, status) VALUES (@id, @h, @ip::inet, 'endpoint', 'active')", c))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("h", "ipv4-" + id.ToString("N")[..8]);
                ins.Parameters.AddWithValue("ip", primary);
                await ins.ExecuteNonQueryAsync();
            }
            if (doc is not null)
            {
                await using var d = new NpgsqlCommand("INSERT INTO zentriq.asset_inventory_documents (asset_id, source, collected_at, document) VALUES (@id, 'agent', now(), @d::jsonb)", c);
                d.Parameters.AddWithValue("id", id);
                d.Parameters.AddWithValue("d", doc);
                await d.ExecuteNonQueryAsync();
            }
            await using var q = new NpgsqlCommand("SELECT zentriq.asset_ipv4(@id)", c);
            q.Parameters.AddWithValue("id", id);
            return await q.ExecuteScalarAsync() as string;
        }

        Assert.Equal("10.1.2.3", await Ip("10.1.2.3", null));
        // IPv6 primary address: the IPv4 address of the adapter with a gateway, link-local skipped.
        Assert.Equal("192.168.1.21", await Ip("fe80::1", """{"network":[{"name":"vEthernet","ipv4":["172.20.0.1"]},{"name":"Wi-Fi","ipv4":["169.254.3.3","192.168.1.21"],"gateway":"192.168.1.1"}]}"""));
        Assert.Equal("172.20.0.1", await Ip("2001:db8::5", """{"network":[{"name":"Ethernet","ipv4":["172.20.0.1"]}]}"""));
        Assert.Null(await Ip("2001:db8::6", """{"network":[{"name":"Ethernet","ipv6":["2001:db8::6"]}]}"""));
    }
}
