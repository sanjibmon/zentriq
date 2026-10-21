using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Accessories;
using Zentriq.Infrastructure.Database;

namespace Zentriq.Tests;

/// <summary>IT accessories in PostgreSQL: stock follows the movements; no negative stock; holdings per person; serials; low stock.</summary>
public sealed class AccessoryStoreTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    private static async Task<Guid> Insert(NpgsqlConnection c, string sql, params (string, object)[] ps)
    {
        var id = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("id", id);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<(int Qty, int Faulty, bool Low)> Stock(NpgsqlConnection c, Guid item, Guid loc)
    {
        await using var cmd = new NpgsqlCommand("SELECT quantity, faulty, low_since IS NOT NULL FROM zentriq.accessory_stock WHERE item_id = @i AND location_id = @l", c);
        cmd.Parameters.AddWithValue("i", item);
        cmd.Parameters.AddWithValue("l", loc);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? (r.GetInt32(0), r.GetInt32(1), r.GetBoolean(2)) : (0, 0, false);
    }

    private static async Task Apply(NpgsqlConnection c, Movement m)
    {
        await using var tx = await c.BeginTransactionAsync();
        await AccessoryStore.ApplyAsync(c, tx, m, default);
        await tx.CommitAsync();
    }

    private static async Task<AccessoryValidationException> Refused(NpgsqlConnection c, Movement m)
    {
        await using var tx = await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<AccessoryValidationException>(() => AccessoryStore.ApplyAsync(c, tx, m, default));
        await tx.RollbackAsync();
        return ex;
    }

    [DbFact]
    public async Task Stock_follows_receive_issue_return_transfer_writeoff_and_count()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var hq = await Insert(c, "INSERT INTO zentriq.locations (id, name) VALUES (@id, @n)", ("n", "HQ store " + tag));
        var pune = await Insert(c, "INSERT INTO zentriq.locations (id, name) VALUES (@id, @n)", ("n", "Pune " + tag));
        var mouse = await Insert(c, "INSERT INTO zentriq.accessory_items (id, name, category, reorder_level) VALUES (@id, @n, 'Mouse', 5)", ("n", "USB mouse " + tag));

        await Apply(c, new Movement("receive", hq, [new MovementLine(mouse, 10, 450m)], Vendor: "Acme", Reference: "INV-1"));
        Assert.Equal((10, 0, false), await Stock(c, mouse, hq));

        await Apply(c, new Movement("issue", hq, [new MovementLine(mouse, 3)], Person: "Asha  Rao", Reason: "New joiner"));
        Assert.Equal(7, (await Stock(c, mouse, hq)).Qty);
        var tooMany = await Refused(c, new Movement("issue", hq, [new MovementLine(mouse, 8)], Person: "Ravi"));
        Assert.Contains("Only 7", tooMany.Message);
        Assert.Equal(7, (await Stock(c, mouse, hq)).Qty);                          // nothing changed

        Assert.Equal(3, await AccessoryStore.HeldAsync(c, null, mouse, "asha rao", default));
        await Apply(c, new Movement("return", hq, [new MovementLine(mouse, 1)], Person: "asha rao", Condition: "faulty"));
        Assert.Equal((7, 1, false), await Stock(c, mouse, hq));
        Assert.Equal(2, await AccessoryStore.HeldAsync(c, null, mouse, "asha rao", default));
        var notHeld = await Refused(c, new Movement("return", hq, [new MovementLine(mouse, 3)], Person: "Asha Rao", Condition: "good"));
        Assert.Contains("only 2", notHeld.Message);

        await Apply(c, new Movement("transfer", hq, [new MovementLine(mouse, 2)], ToLocationId: pune));
        Assert.Equal((5, 1, true), await Stock(c, mouse, hq));                   // 5 <= reorder level 5: low
        Assert.Equal(2, (await Stock(c, mouse, pune)).Qty);

        await Apply(c, new Movement("writeoff", hq, [new MovementLine(mouse, 1)], Condition: "faulty", Reason: "Broken"));
        Assert.Equal((5, 0, true), await Stock(c, mouse, hq));
        await Refused(c, new Movement("writeoff", hq, [new MovementLine(mouse, 1)], Condition: "good"));   // reason required

        await Apply(c, new Movement("adjust", hq, [new MovementLine(mouse, 9)], Reason: "Annual count"));
        Assert.Equal((9, 0, false), await Stock(c, mouse, hq));                   // above the reorder level again
        await Refused(c, new Movement("adjust", hq, [new MovementLine(mouse, 9)]));                      // matches: nothing to correct
    }

    [DbFact]
    public async Task Serial_tracked_units_are_issued_and_returned_by_serial()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var hq = await Insert(c, "INSERT INTO zentriq.locations (id, name) VALUES (@id, @n)", ("n", "Store " + tag));
        var dock = await Insert(c, "INSERT INTO zentriq.accessory_items (id, name, category, serial_tracked) VALUES (@id, @n, 'Docking station', true)", ("n", "Dock " + tag));

        var missing = await Refused(c, new Movement("receive", hq, [new MovementLine(dock, 2, Serials: ["D1"])]));
        Assert.Contains("2 serial number(s)", missing.Message);
        await Apply(c, new Movement("receive", hq, [new MovementLine(dock, 2, Serials: ["D1", "D2"])]));
        var dup = await Refused(c, new Movement("receive", hq, [new MovementLine(dock, 1, Serials: ["d1"])]));
        Assert.Contains("already registered", dup.Message);

        await Apply(c, new Movement("issue", hq, [new MovementLine(dock, 1, Serials: ["D2"])], Person: "Meera"));
        var again = await Refused(c, new Movement("issue", hq, [new MovementLine(dock, 1, Serials: ["D2"])], Person: "Ravi"));
        Assert.Contains("not in stock", again.Message);
        await Apply(c, new Movement("return", hq, [new MovementLine(dock, 1, Serials: ["D2"])], Person: "Meera", Condition: "good"));
        Assert.Equal((2, 0, false), await Stock(c, dock, hq));
        await Refused(c, new Movement("adjust", hq, [new MovementLine(dock, 5)]));     // counted per unit, not by stock count
    }
}
