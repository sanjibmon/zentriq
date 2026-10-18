using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.NonIt;

namespace Zentriq.Tests;

/// <summary>Reading uploaded spreadsheets and writing the Excel form (no database).</summary>
public sealed class NonItSheetTests
{
    [Fact]
    public void Xlsx_written_by_the_book_reads_back_with_dates_numbers_and_text()
    {
        var sheet = new XlsxSheet("Assets");
        sheet.Headings.AddRange(["Asset tag", "Name *", "Purchase date", "Purchase cost", "Notes"]);
        sheet.Rows.Add(["NIT-1", "Chair, ergonomic", new DateOnly(2025, 3, 31), 12500.50m, "line 1\nline 2"]);
        sheet.Rows.Add([null, "Desk", null, 8000, "=1+1"]);
        var rows = SheetReader.Read(XlsxBook.Write(sheet, new XlsxSheet("Lists")), "Assets");
        Assert.Equal(3, rows.Count);
        Assert.Equal("Name *", rows[0][1]);
        Assert.Equal("Chair, ergonomic", rows[1][1]);
        Assert.Equal(new DateOnly(2025, 3, 31), SheetReader.ParseDate(rows[1][2]));
        Assert.Equal(12500.50m, SheetReader.ParseNumber(rows[1][3]));
        Assert.Equal("line 1\nline 2", rows[1][4]);
        Assert.Equal("", rows[2][0]);
        Assert.Equal("=1+1", rows[2][4]);   // stored as text, never as a formula
    }

    [Fact]
    public void Csv_with_quotes_semicolons_and_bom()
    {
        var csv = "﻿Name;Category;Notes\r\n\"Chair; blue\";Office chair;\"He said \"\"ok\"\"\"\r\nDesk;Desk / workstation;\n";
        var rows = SheetReader.Read(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.TrimStart('﻿'))).ToArray());
        Assert.Equal(new[] { "Name", "Category", "Notes" }, rows[0]);
        Assert.Equal(new[] { "Chair; blue", "Office chair", "He said \"ok\"" }, rows[1]);
        Assert.Equal("Desk", rows[2][0]);
    }

    [Theory]
    [InlineData("2026-03-31", 2026, 3, 31)]
    [InlineData("31/03/2026", 2026, 3, 31)]
    [InlineData("31-03-2026", 2026, 3, 31)]
    [InlineData("31 Mar 2026", 2026, 3, 31)]
    [InlineData("46112", 2026, 3, 31)]
    public void Dates_in_common_formats(string text, int y, int m, int d) => Assert.Equal(new DateOnly(y, m, d), SheetReader.ParseDate(text));

    [Fact]
    public void Amounts_and_bad_values()
    {
        Assert.Equal(125000.50m, SheetReader.ParseNumber("₹ 1,25,000.50"));
        Assert.Null(SheetReader.ParseDate("next week"));
        Assert.Null(SheetReader.ParseNumber(""));
        Assert.Throws<InvalidDataException>(() => SheetReader.Read([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]));
    }

    [Fact]
    public void Template_has_the_form_instructions_and_drop_down_lists()
    {
        var lk = new NonItLookups(
            [new(Guid.NewGuid(), "Office chair", "Furniture", true), new(Guid.NewGuid(), "Air conditioner", "Electrical & appliances", true)],
            [(Guid.NewGuid(), "Finance")],
            [new(Guid.NewGuid(), "fuel_type", "Fuel type", "list", ["Petrol", "Diesel", "Electric"], 0)]);
        var bytes = NonItStore.Template(lk);
        var assets = SheetReader.Read(bytes, "Assets");
        Assert.Contains("Name *", assets[0]);
        Assert.Contains("Category *", assets[0]);
        Assert.Contains("Fuel type", assets[0]);
        var lists = SheetReader.Read(bytes, "Lists");
        Assert.Equal("Category", lists[0][0]);
        Assert.Contains(lists, r => r.Length > 0 && r[0] == "Air conditioner");
        Assert.Contains(lists, r => r.Contains("Diesel"));
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        using var s1 = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = s1.ReadToEnd();
        Assert.Contains("dataValidation type=\"list\"", xml);
        Assert.Contains("Lists!$A$2:$A$3", xml);
    }
}

/// <summary>Non-IT assets in PostgreSQL: save with history, bulk upload (check, apply, update, new categories).</summary>
public sealed class NonItStoreTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    private static async Task<T> Scalar<T>(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    [DbFact]
    public async Task Save_numbers_tags_keeps_history_and_rejects_duplicates()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var lk = await NonItStore.LookupsAsync(c, null, default);
        Assert.True(lk.Categories.Count >= 50);
        var chair = lk.Categories.First(x => x.Name == "Office chair");

        await using (var tx = await c.BeginTransactionAsync())
        {
            var bad = await Assert.ThrowsAsync<NonItValidationException>(() => NonItStore.SaveAsync(c, tx, null, new NonItValues { Name = " " }, lk, "t", "created", default));
            Assert.Contains("name", bad.Errors.Keys);
            Assert.Contains("category", bad.Errors.Keys);
        }
        Guid id;
        await using (var tx = await c.BeginTransactionAsync())
        {
            id = await NonItStore.SaveAsync(c, tx, null, new NonItValues { Name = "Ergonomic chair", CategoryId = chair.Id, PurchaseCost = 12000, Quantity = 4 }, lk, "admin@x", "created", default);
            await tx.CommitAsync();
        }
        var tag = await Scalar<string>(c, $"SELECT asset_tag FROM zentriq.nonit_assets WHERE id = '{id}'");
        Assert.Matches("^NIT-\\d{6}$", tag);

        await using (var tx = await c.BeginTransactionAsync())
        {
            var v = (await NonItStore.LoadValuesAsync(c, tx, id, default))!;
            v.Status = "under_repair"; v.Location = "Floor 2";
            await NonItStore.SaveAsync(c, tx, id, v, lk, "admin@x", "updated", default);
            await tx.CommitAsync();
        }
        var changes = await Scalar<string>(c, $"SELECT changes::text FROM zentriq.nonit_history WHERE asset_id = '{id}' AND action = 'updated'");
        Assert.Contains("under_repair", changes);
        Assert.Contains("Floor 2", changes);
        Assert.DoesNotContain("row_version", changes);

        // A second asset with the same tag is refused; a stale row version too.
        await using (var tx = await c.BeginTransactionAsync())
        {
            var dup = await Assert.ThrowsAsync<NonItValidationException>(() => NonItStore.SaveAsync(c, tx, null, new NonItValues { Name = "Other", CategoryId = chair.Id, AssetTag = tag.ToLowerInvariant() }, lk, "t", "created", default));
            Assert.Contains("assetTag", dup.Errors.Keys);
            var stale = await Assert.ThrowsAsync<NonItValidationException>(() => NonItStore.SaveAsync(c, tx, id, new NonItValues { Name = "Chair", CategoryId = chair.Id, RowVersion = 1 }, lk, "t", "updated", default));
            Assert.Contains("rowVersion", stale.Errors.Keys);
        }
    }

    [DbFact]
    public async Task Bulk_upload_checks_rows_then_applies_and_updates_by_tag()
    {
        await using var c = new NpgsqlConnection(_db.AppConnection);
        await c.OpenAsync();
        var lk = await NonItStore.LookupsAsync(c, null, default);
        var rows = new List<string[]>
        {
            new[] { "Asset tag", "Name *", "Category *", "Status", "Purchase date", "Purchase cost", "Warranty until", "Colour" },
            new[] { "FUR-001", "Ergonomic chair", "Office chair", "In use", "31/03/2025", "12,500", "2028-03-31", "Blue" },
            new[] { "", "Split AC 1.5 ton", "Air conditioner", "In store", "", "45000", "", "" },
            new[] { "", "", "", "", "", "", "", "" },
            new[] { "", "Golf cart", "Golf cart", "", "", "", "", "" },
            new[] { "", "Fridge", "Refrigerator", "Broken", "yesterday", "abc", "", "" },
        };
        var check = await NonItImporter.RunAsync(c, rows, new ImportOptions(DryRun: true), "admin@x", default);
        Assert.True(check.DryRun);
        Assert.Equal(4, check.Rows);
        Assert.Contains(check.Errors, e => e.Row == 5 && e.Message.Contains("Golf cart"));
        Assert.Contains(check.Errors, e => e.Row == 6 && e.Message.StartsWith("Status"));
        Assert.Contains(check.Errors, e => e.Row == 6 && e.Message.StartsWith("Purchase date"));
        Assert.Contains(check.Errors, e => e.Row == 6 && e.Message.StartsWith("Purchase cost"));
        Assert.Contains("Colour", check.UnknownColumns);
        Assert.Equal(0L, await Scalar<long>(c, "SELECT count(*) FROM zentriq.nonit_assets"));

        // A file with errors is never applied, even when not a dry run.
        var failed = await NonItImporter.RunAsync(c, rows, new ImportOptions(DryRun: false), "admin@x", default);
        Assert.False(failed.Ok);
        Assert.Equal(0L, await Scalar<long>(c, "SELECT count(*) FROM zentriq.nonit_assets"));

        // Fixed file, missing categories created.
        rows.RemoveAt(5);
        var applied = await NonItImporter.RunAsync(c, rows, new ImportOptions(CreateCategories: true, DryRun: false), "admin@x", default);
        Assert.True(applied.Ok, string.Join("; ", applied.Errors.Select(e => e.Message)));
        Assert.Equal((3, 0), (applied.Created, applied.Updated));
        Assert.Equal(new[] { "Golf cart" }, applied.NewCategories);
        Assert.Equal(12500m, await Scalar<decimal>(c, "SELECT purchase_cost FROM zentriq.nonit_assets WHERE asset_tag = 'FUR-001'"));
        Assert.Equal("in_store", await Scalar<string>(c, "SELECT status FROM zentriq.nonit_assets WHERE name = 'Split AC 1.5 ton'"));
        Assert.Equal(3L, await Scalar<long>(c, "SELECT count(*) FROM zentriq.nonit_history WHERE action = 'imported'"));

        // Same tag again: only the filled cells change.
        var update = new List<string[]> { new[] { "Asset tag", "Name", "Category", "Location" }, new[] { "fur-001", "", "Office chair", "Floor 3" } };
        var upd = await NonItImporter.RunAsync(c, update, new ImportOptions(DryRun: false), "admin@x", default);
        Assert.True(upd.Ok, string.Join("; ", upd.Errors.Select(e => e.Message)));
        Assert.Equal(1, upd.Updated);
        Assert.Equal("Floor 3", await Scalar<string>(c, "SELECT location FROM zentriq.nonit_assets WHERE asset_tag = 'FUR-001'"));
        Assert.Equal("Ergonomic chair", await Scalar<string>(c, "SELECT name FROM zentriq.nonit_assets WHERE asset_tag = 'FUR-001'"));
        Assert.Equal("2028-03-31", await Scalar<string>(c, "SELECT warranty_until::text FROM zentriq.nonit_assets WHERE asset_tag = 'FUR-001'"));
        _ = lk;
    }
}
