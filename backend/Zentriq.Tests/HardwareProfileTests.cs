using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Inventory;

namespace Zentriq.Tests;

/// <summary>Manually entered manufacturer / model / serial number, remembered by the device's fixed identities.</summary>
public sealed class HardwareProfileTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    [Theory]
    [InlineData("To be filled by O.E.M.", null)]
    [InlineData("Default string", null)]
    [InlineData("System Serial Number", null)]
    [InlineData("0000000000", null)]
    [InlineData("   ", null)]
    [InlineData(" PF3XK2LM ", "PF3XK2LM")]
    [InlineData("LENOVO", "LENOVO")]
    public void Placeholders_are_not_values(string input, string? expected) => Assert.Equal(expected, HardwareProfiles.Clean(input));

    private static async Task<Guid> Asset(NpgsqlConnection c, params (string Kind, string Value)[] ids)
    {
        var id = Guid.NewGuid();
        await using (var ins = new NpgsqlCommand("INSERT INTO zentriq.assets (id, hostname, category, status) VALUES (@id, @h, 'endpoint', 'active')", c))
        {
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("h", "hw-" + id.ToString("N")[..8]);
            await ins.ExecuteNonQueryAsync();
        }
        foreach (var (k, v) in ids)
        {
            await using var i = new NpgsqlCommand("INSERT INTO zentriq.asset_identifiers (asset_id, kind, value) VALUES (@a, @k, @v)", c);
            i.Parameters.AddWithValue("a", id);
            i.Parameters.AddWithValue("k", k);
            i.Parameters.AddWithValue("v", v);
            await i.ExecuteNonQueryAsync();
        }
        return id;
    }

    private static async Task<(string? Mf, string? Md, string? Sn)> Manual(NpgsqlConnection c, Guid id)
    {
        await using var q = new NpgsqlCommand("SELECT manual_manufacturer, manual_model, manual_serial FROM zentriq.assets WHERE id = @id", c);
        q.Parameters.AddWithValue("id", id);
        await using var r = await q.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    private static async Task<bool> Restore(NpgsqlConnection c, Guid id, string? uuid, params string[] macs)
    {
        var doc = new InventoryDocument { Device = { SmbiosUuid = uuid } };
        foreach (var m in macs) doc.Network.Add(new NetworkAdapter { Mac = m });
        await using var tx = await c.BeginTransactionAsync();
        var ok = await HardwareProfiles.RestoreAsync(c, tx, id, doc, default);
        await tx.CommitAsync();
        return ok;
    }

    private static async Task Delete(NpgsqlConnection c, Guid id)
    {
        await using var d = new NpgsqlCommand("DELETE FROM zentriq.assets WHERE id = @id", c);
        d.Parameters.AddWithValue("id", id);
        await d.ExecuteNonQueryAsync();
    }

    [DbFact]
    public async Task Entered_values_come_back_when_the_device_is_found_again()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var uuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var mac = "02:" + string.Join(":", Guid.NewGuid().ToByteArray().Take(5).Select(b => b.ToString("X2")));

        var first = await Asset(c, ("smbios_uuid", uuid), ("mac", mac));
        await using (var tx = await c.BeginTransactionAsync())
        {
            Assert.Equal(2, await HardwareProfiles.SaveAsync(c, tx, first, "Acme", " Box 9 ", "SN-77", "admin@example.test", default));
            await tx.CommitAsync();
        }
        Assert.Equal(("Acme", "Box 9", "SN-77"), await Manual(c, first));

        // Agent / Windows reinstalled and the old record deleted: the new record gets the values back.
        await Delete(c, first);
        var again = await Asset(c, ("smbios_uuid", uuid), ("mac", mac));
        Assert.True(await Restore(c, again, uuid, mac));
        Assert.Equal(("Acme", "Box 9", "SN-77"), await Manual(c, again));
        Assert.False(await Restore(c, again, uuid, mac));                           // already has values: nothing to do

        // The same network adapter in another machine (different SMBIOS UUID): not applied.
        var other = await Asset(c);
        Assert.False(await Restore(c, other, Guid.NewGuid().ToString().ToUpperInvariant(), mac));
        Assert.Equal((null, null, null), await Manual(c, other));

        // Clearing removes the profile: a later reinstall gets nothing.
        await using (var tx = await c.BeginTransactionAsync())
        {
            Assert.Equal(0, await HardwareProfiles.SaveAsync(c, tx, again, null, " ", null, "admin@example.test", default));
            await tx.CommitAsync();
        }
        await Delete(c, again);
        var third = await Asset(c, ("smbios_uuid", uuid));
        Assert.False(await Restore(c, third, uuid, mac));
    }
}
