using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Lifecycle;

namespace Zentriq.Tests;

/// <summary>Asset Lifecycle: depreciation, transitions, custody, disposal, automation and tags.</summary>
public sealed class LifecycleTests
{
    [Theory]
    [InlineData("straight_line", 24, 524.67)]          // half of (1000 - 50) depreciated after 24 of 48 months (calendar-day accrual)
    [InlineData("straight_line", 72, 50)]              // fully depreciated: salvage value
    [InlineData("declining_balance", 24, 249.76)]      // double declining: 1000 × 0.5^2
    [InlineData("none", 24, 1000)]
    public void Book_value_follows_the_method(string method, int months, double expected)
    {
        var start = new DateOnly(2024, 1, 1);
        var v = LifecycleStore.BookValue(1000m, 50m, method, 48, start, start.AddMonths(months));
        Assert.Equal((decimal)expected, v!.Value, 0);
    }

    [Fact]
    public void Schedule_ends_at_the_salvage_value()
    {
        var rows = LifecycleStore.Schedule(1200m, 0m, "straight_line", 36, new DateOnly(2025, 1, 1));
        Assert.Equal(2025, rows[0].Year);
        Assert.Equal(0m, rows[^1].Closing);
        Assert.Equal(1200m, rows.Sum(r => r.Depreciation), 0);
    }

    [Fact]
    public void Disposed_is_final_and_in_use_cannot_jump_to_disposed()
    {
        Assert.Empty(LifecycleStore.Transitions["disposed"]);
        Assert.DoesNotContain("disposed", LifecycleStore.Transitions["in_use"]);
        Assert.Contains("disposed", LifecycleStore.Transitions["retired"]);
        Assert.All(LifecycleStore.Transitions.Values.SelectMany(x => x), s => Assert.True(LifecycleStore.IsStage(s)));
    }
}

public sealed class LifecycleDatabaseTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    private static async Task<Guid> Asset(NpgsqlConnection c, string? serial = null, string type = "laptop", bool inventoried = true)
    {
        var id = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.assets (id, hostname, serial_number, category, device_type, status, last_inventory_at, last_seen_at)
            VALUES (@id, @h, @s, 'endpoint', @t, 'active', CASE WHEN @inv THEN now() END, now() - interval '2 hours')
            """, c);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("h", "lc-" + id.ToString("N")[..8]);
        cmd.Parameters.AddWithValue("s", (object?)serial ?? DBNull.Value);
        cmd.Parameters.AddWithValue("t", type);
        cmd.Parameters.AddWithValue("inv", inventoried);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<(string Stage, string Status, string? Person, string AssetStatus, string? Tag)> State(NpgsqlConnection c, Guid id)
    {
        await using var cmd = new NpgsqlCommand("SELECT l.stage, l.status, l.assigned_to, a.status, l.asset_tag FROM zentriq.asset_lifecycle l JOIN zentriq.assets a ON a.id = l.asset_id WHERE l.asset_id = @id", c);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
    }

    private static async Task Change(NpgsqlConnection c, Guid id, StageChange ch)
    {
        await using var tx = await c.BeginTransactionAsync();
        await LifecycleStore.ChangeAsync(c, tx, id, ch, "test", default);
        await tx.CommitAsync();
    }

    private static async Task<LifecycleValidationException> Refused(NpgsqlConnection c, Guid id, StageChange ch)
    {
        await using var tx = await c.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<LifecycleValidationException>(() => LifecycleStore.ChangeAsync(c, tx, id, ch, "test", default));
        await tx.RollbackAsync();
        return ex;
    }

    [DbFact]
    public async Task Assign_loan_return_retire_and_dispose()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var id = await Asset(c);
        Assert.Equal(("in_use", "in_use"), ((await State(c, id)).Stage, (await State(c, id)).Status));      // created by the trigger

        await Refused(c, id, new StageChange("in_use", "assigned"));                                         // no person
        await Change(c, id, new StageChange("in_use", "assigned", AssignedTo: "Asha Rao", AssignedEmail: "asha@example.test"));
        Assert.Equal("Asha Rao", (await State(c, id)).Person);
        await Refused(c, id, new StageChange("in_use", "loaned", AssignedTo: "Ravi"));                      // loan needs a return date
        await Refused(c, id, new StageChange("disposed", "recycled"));                                       // in use cannot be disposed

        await Change(c, id, new StageChange("in_stock", "awaiting_deployment", "Returned"));
        var s = await State(c, id);
        Assert.Equal(("in_stock", null), (s.Stage, s.Person));
        await Refused(c, id, new StageChange("retired", "end_of_life"));                                      // reason required
        await Change(c, id, new StageChange("retired", "pending_disposal", "Broken screen, older than 5 years"));
        Assert.Equal("retired", (await State(c, id)).AssetStatus);                                            // leaves the active inventory

        var noData = await Refused(c, id, new StageChange("disposed", "recycled", DisposedOn: DateOnly.FromDateTime(DateTime.Today)));
        Assert.Equal("sanitizationMethod", noData.Field);
        await Refused(c, id, new StageChange("disposed", "recycled", DisposedOn: DateOnly.FromDateTime(DateTime.Today), SanitizationMethod: "purge"));   // who / when
        await Change(c, id, new StageChange("disposed", "recycled", DisposedOn: DateOnly.FromDateTime(DateTime.Today), SanitizationMethod: "purge",
            SanitizedBy: "IT", SanitizedOn: DateOnly.FromDateTime(DateTime.Today), CertificateRef: "COD-1"));
        Assert.Equal("disposed", (await State(c, id)).Stage);
        await Refused(c, id, new StageChange("in_stock"));                                                      // final

        await using var ev = new NpgsqlCommand("SELECT array_agg(kind ORDER BY id) FROM zentriq.asset_lifecycle_events WHERE asset_id = @id", c);
        ev.Parameters.AddWithValue("id", id);
        Assert.Equal(["assigned", "returned", "stage", "disposed"], (string[])(await ev.ExecuteScalarAsync())!);
    }

    [DbFact]
    public async Task Stock_item_is_deployed_when_first_seen_and_not_seen_devices_become_missing()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var stock = await Asset(c, "SN-" + Guid.NewGuid().ToString("N")[..8], inventoried: false);
        await using (var u = new NpgsqlCommand("UPDATE zentriq.asset_lifecycle SET stage = 'in_stock', status = 'new', auto_deploy = true WHERE asset_id = @id", c))
        { u.Parameters.AddWithValue("id", stock); await u.ExecuteNonQueryAsync(); }

        // Billing ignores items in stock.
        await using (var seen = new NpgsqlCommand("UPDATE zentriq.assets SET last_seen_at = now() WHERE id = @id", c))
        { seen.Parameters.AddWithValue("id", stock); await seen.ExecuteNonQueryAsync(); }
        Assert.Equal("in_use", (await State(c, stock)).Stage);

        var old = await Asset(c);
        await using (var age = new NpgsqlCommand("UPDATE zentriq.assets SET last_seen_at = now() - interval '90 days' WHERE id = @id", c))
        { age.Parameters.AddWithValue("id", old); await age.ExecuteNonQueryAsync(); }
        Assert.True(await LifecycleStore.MarkNotSeenAsync(c, default) >= 1);
        Assert.Equal(("missing", "not_seen"), ((await State(c, old)).Stage, (await State(c, old)).Status));
        await using (var back = new NpgsqlCommand("UPDATE zentriq.assets SET last_seen_at = now() WHERE id = @id", c))
        { back.Parameters.AddWithValue("id", old); await back.ExecuteNonQueryAsync(); }
        Assert.Equal("in_use", (await State(c, old)).Stage);                                                  // seen again
    }

    [DbFact]
    public async Task Tags_use_the_class_prefix_and_never_repeat()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var a = await Asset(c, type: "laptop");
        var b = await Asset(c, type: "switch");
        await using var tx = await c.BeginTransactionAsync();
        var t1 = await LifecycleStore.EnsureTagAsync(c, tx, a, default);
        var t2 = await LifecycleStore.EnsureTagAsync(c, tx, b, default);
        Assert.Equal(t1, await LifecycleStore.EnsureTagAsync(c, tx, a, default));                            // keeps its tag
        await tx.CommitAsync();
        Assert.StartsWith("LAP-", t1);
        Assert.StartsWith("NET-", t2);
    }
}
