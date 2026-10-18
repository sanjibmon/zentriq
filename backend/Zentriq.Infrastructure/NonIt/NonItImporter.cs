using Npgsql;

namespace Zentriq.Infrastructure.NonIt;

public sealed record ImportOptions(bool UpdateExisting = true, bool CreateCategories = false, bool DryRun = true);
public sealed record ImportRowError(int Row, string Message);
public sealed record ImportResult(int Rows, int Created, int Updated, int Skipped, List<ImportRowError> Errors, List<string> NewCategories, List<string> UnknownColumns, bool DryRun)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Bulk upload of non-IT assets from the Excel form (or any sheet / CSV with the same headings). A dry run checks every
/// row and reports problems by spreadsheet row number; the real run applies all rows in one transaction (all or nothing).
/// </summary>
public static class NonItImporter
{
    public static async Task<ImportResult> RunAsync(NpgsqlConnection conn, List<string[]> sheet, ImportOptions o, string? actor, CancellationToken ct)
    {
        var errors = new List<ImportRowError>();
        var newCats = new List<string>();
        var unknown = new List<string>();
        // The heading row: the first row that names "Name" (the form has it in row 1; tolerate a title row above).
        var headerRow = sheet.FindIndex(r => r.Any(c => NonItFields.NormalizeHeading(c) == "name"));
        if (headerRow < 0) return new(0, 0, 0, 0, [new(1, "No heading row found. Download the form and keep its headings (Name and Category are required).")], newCats, unknown, o.DryRun);

        await using var tx = await conn.BeginTransactionAsync(ct);
        var lk = await NonItStore.LookupsAsync(conn, tx, ct);
        var headings = NonItStore.Headings(lk);
        var map = new Dictionary<int, NonItStore.Heading>();
        var header = sheet[headerRow];
        for (var c = 0; c < header.Length; c++)
        {
            var h = NonItFields.NormalizeHeading(header[c]);
            if (h.Length == 0) continue;
            var match = headings.FirstOrDefault(x => NonItFields.NormalizeHeading(x.Title) == h || NonItFields.NormalizeHeading(x.Key.Replace("custom.", "")) == h);
            if (match is null) unknown.Add(header[c].Trim());
            else map[c] = match;
        }
        if (!map.Values.Any(x => x.Key == "category"))
            return new(0, 0, 0, 0, [new(headerRow + 1, "The Category column is missing.")], newCats, unknown, o.DryRun);

        int created = 0, updated = 0, skipped = 0, rows = 0;
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = headerRow + 1; i < sheet.Count; i++)
        {
            var row = sheet[i];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            rows++;
            var rowNo = i + 1;
            string Cell(string key) => map.FirstOrDefault(kv => kv.Value.Key == key) is { Value: not null } kv && kv.Key < row.Length ? row[kv.Key].Trim() : "";
            var problems = new List<string>();

            // An existing asset with the same tag is updated: only the cells filled in the file change.
            Guid? existing = null;
            var tag = Cell("assetTag");
            NonItValues? stored = null;
            if (tag.Length > 0)
            {
                if (!seenTags.Add(tag)) problems.Add($"Asset tag {tag} appears more than once in the file.");
                await using var find = new NpgsqlCommand("SELECT id FROM zentriq.nonit_assets WHERE lower(asset_tag) = lower(@t)", conn, tx);
                find.Parameters.AddWithValue("t", tag);
                if (await find.ExecuteScalarAsync(ct) is Guid gid) { existing = gid; stored = await NonItStore.LoadValuesAsync(conn, tx, gid, ct); }
            }
            if (existing is not null && !o.UpdateExisting) { skipped++; continue; }
            var v = stored ?? new NonItValues();
            v.Custom ??= [];
            v.RowVersion = null;

            foreach (var (col, h) in map)
            {
                var text = col < row.Length ? row[col].Trim() : "";
                if (text.Length == 0) continue;
                if (h.IsCustom) { v.Custom[h.Key["custom.".Length..]] = text; continue; }
                if (h.Key == "assetTag" && existing is not null) continue;   // the tag found the asset; its spelling stays
                var f = NonItFields.All.First(x => x.Key == h.Key);
                switch (f.Type)
                {
                    case NonItFieldType.Date:
                        var d = SheetReader.ParseDate(text);
                        if (d is null) problems.Add($"{f.Label}: \"{text}\" is not a date (use YYYY-MM-DD or DD/MM/YYYY)."); else SetDate(v, f.Key, d);
                        break;
                    case NonItFieldType.Number:
                        var n = SheetReader.ParseNumber(text);
                        if (n is null) problems.Add($"{f.Label}: \"{text}\" is not a number."); else SetNumber(v, f.Key, n);
                        break;
                    case NonItFieldType.Integer:
                        var q = SheetReader.ParseNumber(text);
                        if (q is null || q != Math.Floor(q.Value) || q > int.MaxValue) problems.Add($"{f.Label}: \"{text}\" is not a whole number."); else v.Quantity = (int)q.Value;
                        break;
                    case NonItFieldType.List when f.Key == "status":
                        var st = NonItFields.Statuses.FirstOrDefault(x => Same(x, text) || Same(NonItFields.StatusLabel(x), text) || (x == "lost" && Same("lost", text)));
                        if (st is null) problems.Add($"Status: \"{text}\" is not one of {string.Join(", ", NonItFields.Statuses.Select(NonItFields.StatusLabel))}."); else v.Status = st;
                        break;
                    case NonItFieldType.List when f.Key == "condition":
                        var co = NonItFields.Conditions.FirstOrDefault(x => Same(x, text));
                        if (co is null) problems.Add($"Condition: \"{text}\" is not one of New, Good, Fair, Poor, Damaged."); else v.Condition = co;
                        break;
                    case NonItFieldType.List when f.Key == "department":
                        var dep = lk.Departments.FirstOrDefault(x => Same(x.Name, text));
                        if (dep.Id == Guid.Empty) problems.Add($"Department: \"{text}\" does not exist. Add it under Departments first."); else v.DepartmentId = dep.Id;
                        break;
                    case NonItFieldType.List when f.Key == "category":
                    {
                        var cat = lk.Categories.FirstOrDefault(x => Same(x.Name, text));
                        if (cat is null && o.CreateCategories)
                        {
                            await using var add = new NpgsqlCommand("INSERT INTO zentriq.nonit_categories (name, group_name) VALUES (@n, 'Custom') ON CONFLICT DO NOTHING RETURNING id", conn, tx);
                            add.Parameters.AddWithValue("n", text.Length > 100 ? text[..100] : text);
                            if (await add.ExecuteScalarAsync(ct) is Guid nid) { cat = new(nid, text, "Custom", false); lk.Categories.Add(cat); newCats.Add(text); }
                        }
                        if (cat is null) problems.Add($"Category: \"{text}\" does not exist. Add it in Zentriq, or tick \"create missing categories\".");
                        else v.CategoryId = cat.Id;
                        break;
                    }
                    default:
                        SetText(v, f.Key, text);
                        break;
                }
            }

            foreach (var (k, msg) in NonItStore.Validate(v, lk)) if (!problems.Any(p => p.StartsWith(Label(k), StringComparison.Ordinal))) problems.Add(msg);
            if (problems.Count > 0) { errors.AddRange(problems.Select(p => new ImportRowError(rowNo, p))); continue; }

            await using (var sp = new NpgsqlCommand("SAVEPOINT row_sp", conn, tx)) await sp.ExecuteNonQueryAsync(ct);
            try
            {
                await NonItStore.SaveAsync(conn, tx, existing, v, lk, actor, existing is null ? "imported" : "import-updated", ct);
                if (existing is null) created++; else updated++;
                await using var rel = new NpgsqlCommand("RELEASE SAVEPOINT row_sp", conn, tx);
                await rel.ExecuteNonQueryAsync(ct);
            }
            catch (NonItValidationException ex)
            {
                await using var rb = new NpgsqlCommand("ROLLBACK TO SAVEPOINT row_sp", conn, tx);
                await rb.ExecuteNonQueryAsync(ct);
                errors.AddRange(ex.Errors.Values.Select(m => new ImportRowError(rowNo, m)));
            }
        }

        if (o.DryRun || errors.Count > 0) await tx.RollbackAsync(ct);
        else await tx.CommitAsync(ct);
        return new(rows, created, updated, skipped, errors.OrderBy(e => e.Row).Take(500).ToList(), newCats, unknown, o.DryRun || errors.Count > 0);
    }

    private static bool Same(string a, string b) => string.Equals(a.Replace("_", " ").Trim(), b.Replace("_", " ").Trim(), StringComparison.OrdinalIgnoreCase);
    private static string Label(string key) => NonItFields.All.FirstOrDefault(f => f.Key == key)?.Label ?? key;

    private static void SetText(NonItValues v, string key, string s)
    {
        switch (key)
        {
            case "assetTag": v.AssetTag = s; break;
            case "name": v.Name = s; break;
            case "manufacturer": v.Manufacturer = s; break;
            case "model": v.Model = s; break;
            case "serialNumber": v.SerialNumber = s; break;
            case "barcode": v.Barcode = s; break;
            case "location": v.Location = s; break;
            case "assignedTo": v.AssignedTo = s; break;
            case "vendor": v.Vendor = s; break;
            case "invoiceNumber": v.InvoiceNumber = s; break;
            case "poNumber": v.PoNumber = s; break;
            case "amcVendor": v.AmcVendor = s; break;
            case "notes": v.Notes = s; break;
        }
    }

    private static void SetDate(NonItValues v, string key, DateOnly? d)
    {
        switch (key)
        {
            case "purchaseDate": v.PurchaseDate = d; break;
            case "warrantyUntil": v.WarrantyUntil = d; break;
            case "amcUntil": v.AmcUntil = d; break;
            case "insuranceUntil": v.InsuranceUntil = d; break;
            case "lastMaintenance": v.LastMaintenance = d; break;
            case "nextMaintenance": v.NextMaintenance = d; break;
        }
    }

    private static void SetNumber(NonItValues v, string key, decimal? n)
    {
        switch (key)
        {
            case "purchaseCost": v.PurchaseCost = n; break;
            case "usefulLifeYears": v.UsefulLifeYears = n; break;
            case "salvageValue": v.SalvageValue = n; break;
        }
    }
}
