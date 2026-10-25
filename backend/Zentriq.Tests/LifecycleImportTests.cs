using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Lifecycle;
using Zentriq.Infrastructure.NonIt;

namespace Zentriq.Tests;

/// <summary>Asset Lifecycle: own asset tag formats, custom fields, people directory and bulk upload.</summary>
public sealed class LifecycleTagFormatTests
{
    [Theory]
    [InlineData("{PREFIX}-{SEQ:6}", "LAP-000042")]
    [InlineData("{ORG}/{YYYY}/{LOC}/{SEQ:4}", "ACME/2026/PUNE/0042")]
    [InlineData("IT{YY}{MM}-{SEQ}", "IT2610-000042")]
    [InlineData("{ORG}-{PREFIX}-{SEQ:3}", "ACME-LAP-042")]
    public void Formats_render(string format, string expected)
    {
        Assert.Null(TagFormat.Validate(format));
        Assert.Equal(expected, TagFormat.Render(format, "LAP", "ACME", "PUNE", new DateOnly(2026, 10, 25), 42));
    }

    [Fact]
    public void Empty_organisation_code_leaves_no_stray_separator_and_counters_restart_per_key()
    {
        Assert.Equal("LAP-000001", TagFormat.Render("{ORG}-{PREFIX}-{SEQ:6}", "LAP", "", "", new DateOnly(2026, 1, 1), 1));
        Assert.NotEqual(TagFormat.CounterKey("{YYYY}-{SEQ}", "LAP", "", "", new DateOnly(2026, 1, 1)), TagFormat.CounterKey("{YYYY}-{SEQ}", "LAP", "", "", new DateOnly(2027, 1, 1)));
        Assert.Equal("LAP-#", TagFormat.CounterKey("{PREFIX}-{SEQ:6}", "LAP", "", "", new DateOnly(2026, 1, 1)));   // build 80 counters carry over
        Assert.Equal("PUNE", TagFormat.LocationCode(null, "Pune office"));
        Assert.Equal("BLR1", TagFormat.LocationCode("BLR1", "Bengaluru"));
    }

    [Theory]
    [InlineData("{PREFIX}-")]                 // no number
    [InlineData("{SEQ}-{SEQ}")]               // two numbers
    [InlineData("{PREFIX} {SEQ}")]            // space
    [InlineData("{FOO}-{SEQ}")]               // unknown token
    [InlineData("{SEQ:12}")]                  // too many digits
    [InlineData("VERYLONGORGANISATIONPREFIX-{ORG}-{PREFIX}-{SEQ:10}")]
    public void Bad_formats_are_refused(string format) => Assert.NotNull(TagFormat.Validate(format));

    [Fact]
    public void Custom_field_values_are_checked_by_type()
    {
        var list = new LcField(Guid.NewGuid(), "cost_centre", "Cost centre", "list", ["CC-100", "CC-200"], true, 0);
        Assert.Equal("CC-200", LifecycleFields.Parse(list, "cc-200")!.ToString());
        Assert.Throws<LifecycleValidationException>(() => LifecycleFields.Parse(list, "CC-999"));
        Assert.Throws<LifecycleValidationException>(() => LifecycleFields.Parse(list, ""));          // required
        var num = new LcField(Guid.NewGuid(), "insured_value", "Insured value", "number", [], false, 0);
        Assert.Equal(125000.50m, LifecycleFields.Parse(num, "1,25,000.50")!.GetValue<decimal>());
        Assert.Null(LifecycleFields.Parse(num, " "));
        var yes = new LcField(Guid.NewGuid(), "byod", "BYOD", "yesno", [], false, 0);
        Assert.Equal("Yes", LifecycleFields.Show(LifecycleFields.Parse(yes, "Y")));
        var date = new LcField(Guid.NewGuid(), "insured_until", "Insured until", "date", [], false, 0);
        Assert.Equal("2027-03-31", LifecycleFields.Parse(date, "31/03/2027")!.ToString());
        Assert.Equal("cost_centre", LifecycleFields.KeyOf("Cost centre"));
        Assert.Equal("f_2nd_owner", LifecycleFields.KeyOf("2nd owner"));
    }
}

public sealed class LifecycleImportDatabaseTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    private static async Task Exec(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
    private static async Task<T?> Scalar<T>(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T?)await cmd.ExecuteScalarAsync();
    }

    [DbFact]
    public async Task Own_tag_format_and_manual_mode()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        await Exec(c, """
            UPDATE zentriq.system_settings SET value = '"{ORG}-{PREFIX}-{SEQ:4}"' WHERE key = 'lifecycle.tag_format';
            UPDATE zentriq.system_settings SET value = '"ACME"' WHERE key = 'lifecycle.tag_org';
            UPDATE zentriq.system_settings SET value = '500' WHERE key = 'lifecycle.tag_start';
            """);
        var sheet = new List<string[]>
        {
            new[] { "Asset tag", "Serial number *", "Device type", "Model" },
            new[] { "", "FMT-" + Guid.NewGuid().ToString("N")[..6], "Laptop", "X1" },
            new[] { "MY-OWN-7", "FMT-" + Guid.NewGuid().ToString("N")[..6], "Desktop", "Z2" },
        };
        var r = await LifecycleImport.RunAsync(c, "assets", sheet, new LcImportOptions(DryRun: false), "test", default);
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Message)));
        Assert.Equal(2, r.Created);
        Assert.Equal(1L, await Scalar<long>(c, "SELECT count(*) FROM zentriq.asset_lifecycle WHERE asset_tag = 'ACME-LAP-0500'"));
        Assert.Equal(1L, await Scalar<long>(c, "SELECT count(*) FROM zentriq.asset_lifecycle WHERE asset_tag = 'MY-OWN-7'"));

        await Exec(c, "UPDATE zentriq.system_settings SET value = '\"manual\"' WHERE key = 'lifecycle.tag_mode'");
        var r2 = await LifecycleImport.RunAsync(c, "assets", [["Serial number"], ["MAN-" + Guid.NewGuid().ToString("N")[..6]]], new LcImportOptions(DryRun: false), "test", default);
        Assert.True(r2.Ok);
        Assert.Equal(1L, await Scalar<long>(c, "SELECT count(*) FROM zentriq.asset_lifecycle l JOIN zentriq.assets a ON a.id = l.asset_id WHERE a.serial_number LIKE 'MAN-%' AND l.asset_tag IS NULL"));
        await Exec(c, "UPDATE zentriq.system_settings SET value = '\"auto\"' WHERE key = 'lifecycle.tag_mode'");
    }

    [DbFact]
    public async Task Asset_upload_checks_creates_updates_and_is_all_or_nothing()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        await Exec(c, "INSERT INTO zentriq.lifecycle_fields (key, label, type, options) VALUES ('cost_centre', 'Cost centre', 'list', '{CC-100,CC-200}') ON CONFLICT DO NOTHING");
        var sn1 = "UP-" + Guid.NewGuid().ToString("N")[..8];
        var sn2 = "UP-" + Guid.NewGuid().ToString("N")[..8];
        var header = new[] { "Asset tag", "Serial number *", "Device type", "Stage", "Assigned to", "Department", "Location", "Purchase date", "Purchase cost", "Warranty end", "Cost centre", "Colour" };
        var good = new List<string[]>
        {
            header,
            new[] { "", sn1, "Laptop", "In use", "Imported Person", "Upload Dept", "Upload Site", "15/01/2025", "₹ 85,000", "2028-01-14", "CC-100", "red" },
            new[] { "", sn2, "Desktop", "In stock", "", "", "", "45658", "60000", "", "", "" },
        };
        // Check only: nothing is written.
        var check = await LifecycleImport.RunAsync(c, "assets", good, new LcImportOptions(DryRun: true), "test", default);
        Assert.True(check.Ok, string.Join("; ", check.Errors.Select(e => e.Message)));
        Assert.Equal((2, 2, 0), (check.Rows, check.Created, check.Updated));
        Assert.Contains("Colour", check.UnknownColumns);
        Assert.Contains("Upload Dept", check.NewDepartments);
        Assert.Equal(0L, await Scalar<long>(c, $"SELECT count(*) FROM zentriq.assets WHERE serial_number = '{sn1}'"));

        var done = await LifecycleImport.RunAsync(c, "assets", good, new LcImportOptions(DryRun: false), "test", default);
        Assert.True(done.Ok);
        Assert.Equal("in_use|assigned|Imported Person|85000.00|CC-100",
            await Scalar<string>(c, $"""
                SELECT l.stage || '|' || l.status || '|' || l.assigned_to || '|' || l.purchase_cost || '|' || (l.custom ->> 'cost_centre')
                  FROM zentriq.asset_lifecycle l JOIN zentriq.assets a ON a.id = l.asset_id WHERE a.serial_number = '{sn1}'
                """));
        Assert.Equal(1L, await Scalar<long>(c, "SELECT count(*) FROM zentriq.lifecycle_people WHERE name = 'Imported Person'"));
        Assert.Equal("2025-01-01", (await Scalar<DateTime>(c, $"SELECT l.purchase_date::timestamp FROM zentriq.asset_lifecycle l JOIN zentriq.assets a ON a.id = l.asset_id WHERE a.serial_number = '{sn2}'")).ToString("yyyy-MM-dd"));

        // Same serial again: update (only filled cells), stage change through the lifecycle rules.
        var tag = await Scalar<string>(c, $"SELECT l.asset_tag FROM zentriq.asset_lifecycle l JOIN zentriq.assets a ON a.id = l.asset_id WHERE a.serial_number = '{sn2}'");
        var upd = await LifecycleImport.RunAsync(c, "assets",
            [["Asset tag", "Serial number", "Vendor", "Stage", "Status"], [tag!, "", "New Vendor", "In maintenance", "Repair"]], new LcImportOptions(DryRun: false), "test", default);
        Assert.True(upd.Ok, string.Join("; ", upd.Errors.Select(e => e.Message)));
        Assert.Equal(1, upd.Updated);
        Assert.Equal("in_maintenance|New Vendor|60000.00", await Scalar<string>(c, $"SELECT stage || '|' || vendor || '|' || purchase_cost FROM zentriq.asset_lifecycle WHERE asset_tag = '{tag}'"));

        // One bad row: nothing is saved, and every problem is reported by row.
        var sn3 = "UP-" + Guid.NewGuid().ToString("N")[..8];
        var bad = await LifecycleImport.RunAsync(c, "assets",
        [
            header,
            new[] { "", sn3, "Laptop", "", "", "", "", "", "", "", "", "" },
            new[] { "", "", "Toaster", "Disposed", "", "", "", "not a date", "abc", "", "CC-999", "" },
            new[] { "", sn3, "", "", "", "", "", "", "", "", "", "" },
        ], new LcImportOptions(DryRun: false, CreateMissing: false), "test", default);
        Assert.False(bad.Ok);
        Assert.Contains(bad.Errors, e => e.Row == 3);
        Assert.Contains(bad.Errors, e => e.Row == 4 && e.Message.Contains("more than once"));
        Assert.Equal(0L, await Scalar<long>(c, $"SELECT count(*) FROM zentriq.assets WHERE serial_number = '{sn3}'"));
    }

    [DbFact]
    public async Task People_and_contracts_upload()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var p = await LifecycleImport.RunAsync(c, "people",
        [
            ["Name *", "E-mail", "Employee ID", "Department", "Status"],
            ["Ravi Kumar", "ravi@example.test", "E-1", "Finance", "Active"],
            ["Meera Iyer", "meera@example.test", "E-2", "", "Left"],
            ["Bad Mail", "not-an-email", "", "", ""],
        ], new LcImportOptions(DryRun: false), "test", default);
        Assert.False(p.Ok);
        Assert.Single(p.Errors, e => e.Row == 4);
        Assert.Equal(0L, await Scalar<long>(c, "SELECT count(*) FROM zentriq.lifecycle_people WHERE name = 'Ravi Kumar'"));   // all or nothing

        p = await LifecycleImport.RunAsync(c, "people",
            [["Name *", "E-mail", "Employee ID", "Department", "Status"], ["Ravi Kumar", "ravi@example.test", "E-1", "Finance", "Active"], ["Meera Iyer", "meera@example.test", "E-2", "", "Left"]],
            new LcImportOptions(DryRun: false), "test", default);
        Assert.True(p.Ok);
        Assert.Equal(2, p.Created);
        p = await LifecycleImport.RunAsync(c, "people", [["Name", "Employee ID", "Job title"], ["Ravi K.", "E-1", "Analyst"]], new LcImportOptions(DryRun: false), "test", default);
        Assert.Equal(1, p.Updated);                                                     // matched by employee ID, renamed
        Assert.Equal("Ravi K.|Analyst|left", await Scalar<string>(c, "SELECT (SELECT name || '|' || title FROM zentriq.lifecycle_people WHERE employee_id = 'E-1') || '|' || (SELECT status FROM zentriq.lifecycle_people WHERE employee_id = 'E-2')"));

        var k = await LifecycleImport.RunAsync(c, "contracts",
            [["Contract name *", "Type *", "End date", "Asset tags"], ["CI AMC", "AMC", "2027-03-31", "NO-SUCH-TAG"]], new LcImportOptions(DryRun: false), "test", default);
        Assert.False(k.Ok);
        Assert.Contains("NO-SUCH-TAG", k.Errors[0].Message);
        k = await LifecycleImport.RunAsync(c, "contracts", [["Contract name *", "Type *", "End date", "Cost"], ["CI AMC", "amc", "31/03/2027", "1,00,000"]], new LcImportOptions(DryRun: false), "test", default);
        Assert.True(k.Ok);
        Assert.Equal("amc|100000.00", await Scalar<string>(c, "SELECT kind || '|' || cost FROM zentriq.asset_contracts WHERE name = 'CI AMC'"));
    }

    [DbFact]
    public async Task Templates_open_and_their_headings_are_read_back()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        foreach (var kind in LifecycleImport.Kinds)
        {
            var file = await LifecycleImport.TemplateAsync(c, kind, default);
            var rows = SheetReader.Read(file, kind switch { "people" => "People", "contracts" => "Contracts", _ => "Assets" });
            var fields = await LifecycleFields.LoadAsync(c, null, default);
            Assert.Equal(LifecycleImport.Columns(kind, fields).Select(x => x.Title), rows[0]);
            // The example row checks cleanly.
            var check = await LifecycleImport.RunAsync(c, kind, rows, new LcImportOptions(DryRun: true), "test", default);
            Assert.True(check.Ok, kind + ": " + string.Join("; ", check.Errors.Select(e => e.Message)));
        }
    }
}
