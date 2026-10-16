using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Application.Licensing;
using Zentriq.Domain.Licensing;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Integrations;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Infrastructure.Reports;
using Zentriq.Inventory;

namespace Zentriq.Tests;

public sealed class InventoryChangeTests
{
    internal static InventoryDocument Doc(string source = "agent", params string[] software)
    {
        var d = new InventoryDocument { Source = source };
        d.Device.Manufacturer = "Dell Inc.";
        d.Device.Model = "Latitude 5440";
        d.Device.SerialNumber = "SN-1";
        d.Os.Family = "windows";
        d.Os.Name = "Windows 11 Pro";
        d.Os.Version = "10.0.22631";
        d.Os.DisplayVersion = "23H2";
        d.Os.Ubr = 4000;
        d.Hardware.CpuName = "Intel Core i5-1345U";
        d.Hardware.CpuCores = 10;
        d.Hardware.MemoryBytes = 8L * 1024 * 1024 * 1024;
        d.Hardware.BiosVersion = "1.10.0";
        d.Hardware.Disks.Add(new Disk { Model = "Samsung PM9A1", Serial = "S1", SizeBytes = 512_110_190_592 });
        d.Network.Add(new NetworkAdapter { Name = "Intel Wi-Fi 6E AX211", Mac = "AA:BB:CC:00:00:01" });
        d.Network.Add(new NetworkAdapter { Name = "Cisco AnyConnect Virtual Miniport", Mac = "02:00:4C:4F:4F:50" });
        d.Patches.Add(new PatchItem { Id = "KB5031455" });
        foreach (var s in software) d.Software.Add(new SoftwareItem { Name = s, Version = "1.0" });
        return d;
    }

    private static List<InstalledSoftware> Sw(params (string Name, string Version)[] items) => items.Select(i => new InstalledSoftware(i.Name, i.Version, null)).ToList();

    [Fact]
    public void First_inventory_or_another_collection_method_records_nothing()
    {
        Assert.Empty(InventoryChanges.Compare(null, null, Doc()));
        var after = Doc("winrm");
        after.Hardware.MemoryBytes = 16L * 1024 * 1024 * 1024;
        Assert.Empty(InventoryChanges.Compare(Doc("agent"), Sw(), after));
    }

    [Fact]
    public void Hardware_replacements_are_detected()
    {
        var before = Doc();
        var after = Doc();
        after.Hardware.MemoryBytes = 16L * 1024 * 1024 * 1024;
        after.Hardware.Disks.Clear();
        after.Hardware.Disks.Add(new Disk { Model = "WD SN740", Serial = "W9", SizeBytes = 1_024_209_543_168 });
        after.Hardware.BiosVersion = "1.12.1";
        after.Device.SerialNumber = "SN-2";
        after.Network[0].Mac = "AA:BB:CC:00:00:02";
        after.Network[1].Mac = "02:00:4C:4F:4F:51";            // virtual VPN adapter: ignored
        var c = InventoryChanges.Compare(before, Sw(), after);
        Assert.Contains(c, x => x is { Kind: "hardware", Change: "added", Component: "Memory", OldValue: "8 GB", NewValue: "16 GB" });
        Assert.Contains(c, x => x is { Component: "Disk", Change: "added" } && x.Item.Contains("WD SN740") && x.Item.Contains("1 TB"));
        Assert.Contains(c, x => x is { Component: "Disk", Change: "removed" } && x.Item.Contains("Samsung"));
        Assert.Contains(c, x => x is { Component: "BIOS / firmware", OldValue: "1.10.0", NewValue: "1.12.1" });
        Assert.Contains(c, x => x is { Component: "System", Item: "Serial number", NewValue: "SN-2" });
        Assert.Equal(2, c.Count(x => x.Component == "Network adapter"));
        Assert.DoesNotContain(c, x => x.Item.Contains("AnyConnect"));
    }

    [Fact]
    public void Missing_values_are_not_changes()
    {
        var after = Doc();
        after.Hardware.MemoryBytes = null;
        after.Hardware.Disks.Clear();
        after.Device.SerialNumber = null;
        after.Hardware.BiosVersion = " 1.10.0 ";
        Assert.Empty(InventoryChanges.Compare(Doc(), Sw(), after));
    }

    [Fact]
    public void Software_installs_uninstalls_and_updates()
    {
        var before = Sw(("7-Zip 23.01 (x64)", "23.01"), ("Google Chrome", "128.0.6613.120"), ("Zoom", "6.1"));
        var after = Doc("agent");
        after.Software.Add(new SoftwareItem { Name = "7-Zip 23.01 (x64)", Version = "23.01" });
        after.Software.Add(new SoftwareItem { Name = "google chrome", Version = "129.0.6668.59" });
        after.Software.Add(new SoftwareItem { Name = "Notepad++", Version = "8.6.9" });
        after.Patches.Add(new PatchItem { Id = "KB5044285", Description = "Security Update" });
        var c = InventoryChanges.Compare(Doc(), before, after);
        Assert.Contains(c, x => x is { Kind: "software", Change: "installed", Component: "Application", Item: "Notepad++", NewValue: "8.6.9" });
        Assert.Contains(c, x => x is { Change: "uninstalled", Item: "Zoom", OldValue: "6.1" });
        Assert.Contains(c, x => x is { Change: "updated", OldValue: "128.0.6613.120", NewValue: "129.0.6668.59" });
        Assert.Contains(c, x => x is { Component: "Windows update", Item: "KB5044285", Change: "installed" });
        Assert.DoesNotContain(c, x => x.Item.StartsWith("7-Zip"));
    }

    [Fact]
    public void Incomplete_software_list_is_not_mass_uninstallation()
    {
        var before = Sw(Enumerable.Range(1, 40).Select(i => ($"App {i}", "1.0")).ToArray());
        var after = Doc("agent", "App 1", "App 2");
        Assert.DoesNotContain(InventoryChanges.Compare(Doc(), before, after), x => x.Change == "uninstalled");
        Assert.Empty(InventoryChanges.Compare(Doc(), before, Doc("agent")).Where(x => x.Component == "Application"));
    }

    [Fact]
    public void Os_upgrade_is_a_change_but_a_cumulative_update_is_not()
    {
        var cu = Doc();
        cu.Os.Ubr = 4317;
        Assert.DoesNotContain(InventoryChanges.Compare(Doc(), Sw(), cu), x => x.Component == "Operating system");
        var upgrade = Doc();
        upgrade.Os.Version = "10.0.26100";
        upgrade.Os.DisplayVersion = "24H2";
        upgrade.Os.Ubr = 2033;
        var c = Assert.Single(InventoryChanges.Compare(Doc(), Sw(), upgrade), x => x.Component == "Operating system");
        Assert.Contains("23H2", c.OldValue);
        Assert.Contains("24H2", c.NewValue);
    }

    [Fact]
    public void Department_column_and_changes_dataset_exist()
    {
        foreach (var d in new[] { "assets", "hardware", "software", "vulnerabilities", "security", "changes" })
            Assert.NotNull(ReportCatalog.Find(d)!.Column("department"));
        Assert.NotNull(ReportCatalog.Find("changes")!.Column("component"));
        Assert.True(EventTypes.Exists("hardware.changed") && EventTypes.Exists("software.changed"));
    }
}

/// <summary>On PostgreSQL: changes recorded by the inventory store, departments in reports.</summary>
public sealed class DepartmentsAndChangesDatabaseTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    private static LicenseStatus Trial(int devices) => new(LicenseMode.Trial, "trial", null, 14, ["*"], new Dictionary<string, int> { ["devices"] = devices }, null, null);

    [DbFact]
    public async Task Inventory_changes_are_stored_and_reported_with_the_department()
    {
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.0.0-test");
        await using var conn = new NpgsqlConnection(_db.AppConnection);
        await conn.OpenAsync();
        var store = new InventoryStore(new LicensingService(null!, null!, null!, null!, null!));
        var agent = Guid.NewGuid();
        var first = InventoryChangeTests.Doc("agent", "Google Chrome", "Zoom");
        first.Device.Hostname = "LT-0042";
        first.Device.Category = "endpoint";
        var r1 = await store.ApplyAsync(conn, first, new MatchHints(AgentId: agent), Trial(30), default);
        Assert.True(r1.Created);

        var second = InventoryChangeTests.Doc("agent", "Google Chrome", "Notepad++");
        second.Device.Hostname = "LT-0042";
        second.Device.Category = "endpoint";
        second.Hardware.MemoryBytes = 32L * 1024 * 1024 * 1024;
        var r2 = await store.ApplyAsync(conn, second, new MatchHints(AgentId: agent), Trial(30), default);
        Assert.Equal(r1.AssetId, r2.AssetId);

        var rows = new List<(string Kind, string Change, string Component, string Item)>();
        await using (var c = new NpgsqlCommand("SELECT kind, change, component, item FROM zentriq.asset_changes WHERE asset_id = @a ORDER BY id", conn))
        {
            c.Parameters.AddWithValue("a", r1.AssetId!.Value);
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }
        Assert.Contains(("hardware", "added", "Memory", "Installed memory"), rows);
        Assert.Contains(("software", "installed", "Application", "Notepad++"), rows);
        Assert.Contains(("software", "uninstalled", "Application", "Zoom"), rows);
        Assert.Equal(3, rows.Count);

        // Department: create, assign, report.
        await using (var c = new NpgsqlCommand("""
            INSERT INTO zentriq.departments (name) VALUES ('Finance');
            UPDATE zentriq.assets SET department_id = (SELECT id FROM zentriq.departments WHERE name = 'Finance') WHERE id = @a;
            """, conn))
        {
            c.Parameters.AddWithValue("a", r1.AssetId!.Value);
            await c.ExecuteNonQueryAsync();
        }
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var byDep = await ReportEngine.RunAsync(ds, "assets", new ReportSpec { Columns = ["department"], GroupBy = ["department"] }, 10, default);
        Assert.Equal("Finance", Assert.Single(byDep.Rows)[0]);
        var hw = await ReportEngine.RunAsync(ds, "changes", new ReportSpec
        {
            Columns = ReportCatalog.Find("changes")!.Columns.Select(c => c.Key).ToList(),
            Filters = [new() { Column = "kind", Op = "eq", Value = "hardware" }],
        }, 10, default);
        Assert.Equal(1, hw.Total);
        foreach (var name in new[] { "Assets by department", "Asset register by department", "Hardware changes (30 days)", "Software changes (30 days)" })
        {
            await using var c = new NpgsqlCommand("SELECT dataset, definition::text FROM zentriq.report_definitions WHERE system AND name = @n", conn);
            c.Parameters.AddWithValue("n", name);
            await using var r = await c.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync(), name);
            var (dataset, def) = (r.GetString(0), r.GetString(1));
            await r.CloseAsync();
            await ReportEngine.RunAsync(ds, dataset, ReportSpec.Parse(def), 10, default);
        }
        // Deleting a department leaves its assets unassigned.
        await using (var c = new NpgsqlCommand("DELETE FROM zentriq.departments WHERE name = 'Finance'", conn))
            await c.ExecuteNonQueryAsync();
        await using (var c = new NpgsqlCommand("SELECT count(*) FROM zentriq.assets WHERE department_id IS NOT NULL", conn))
            Assert.Equal(0L, await c.ExecuteScalarAsync());
    }
}
