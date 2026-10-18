using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.NonIt;

/// <summary>The editable values of a non-IT asset (form, API and Excel import).</summary>
public sealed class NonItValues
{
    public string? AssetTag { get; set; }
    public string? Name { get; set; }
    public Guid? CategoryId { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? Barcode { get; set; }
    public string? Status { get; set; }
    public string? Condition { get; set; }
    public int? Quantity { get; set; }
    public string? Location { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? AssignedTo { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public string? Vendor { get; set; }
    public decimal? PurchaseCost { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? PoNumber { get; set; }
    public DateOnly? WarrantyUntil { get; set; }
    public string? AmcVendor { get; set; }
    public DateOnly? AmcUntil { get; set; }
    public DateOnly? InsuranceUntil { get; set; }
    public DateOnly? LastMaintenance { get; set; }
    public DateOnly? NextMaintenance { get; set; }
    public decimal? UsefulLifeYears { get; set; }
    public decimal? SalvageValue { get; set; }
    public string? Notes { get; set; }
    public Dictionary<string, string?>? Custom { get; set; }
    public int? RowVersion { get; set; }
}

public sealed record NonItCategory(Guid Id, string Name, string Group, bool Builtin);
public sealed record NonItCustomField(Guid Id, string Key, string Label, string Type, string[] Options, int Sort);
public sealed record NonItLookups(List<NonItCategory> Categories, List<(Guid Id, string Name)> Departments, List<NonItCustomField> Fields);

public sealed class NonItValidationException(Dictionary<string, string> errors) : Exception(string.Join("; ", errors.Values))
{
    public Dictionary<string, string> Errors { get; } = errors;
}

/// <summary>Non-IT assets: lookups, validation, save with history, Excel template and export rows.</summary>
public static class NonItStore
{
    public static async Task<NonItLookups> LookupsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var cats = new List<NonItCategory>();
        await using (var c = new NpgsqlCommand("SELECT id, name, group_name, builtin FROM zentriq.nonit_categories ORDER BY group_name, name", conn, tx))
        await using (var r = await c.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) cats.Add(new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)));
        var deps = new List<(Guid, string)>();
        await using (var c = new NpgsqlCommand("SELECT id, name FROM zentriq.departments ORDER BY name", conn, tx))
        await using (var r = await c.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) deps.Add((r.GetGuid(0), r.GetString(1)));
        var fields = new List<NonItCustomField>();
        await using (var c = new NpgsqlCommand("SELECT id, key, label, type, options, sort FROM zentriq.nonit_fields ORDER BY sort, label", conn, tx))
        await using (var r = await c.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) fields.Add(new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4), r.GetInt32(5)));
        return new(cats, deps, fields);
    }

    /// <summary>Checks the values; returns field → message for every problem (empty when valid).</summary>
    public static Dictionary<string, string> Validate(NonItValues v, NonItLookups lk)
    {
        var e = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(v.Name)) e["name"] = "Enter the item's name.";
        if (v.CategoryId is null) e["category"] = "Choose a category.";
        else if (lk.Categories.All(c => c.Id != v.CategoryId)) e["category"] = "This category no longer exists.";
        if (v.Status is not null && !NonItFields.Statuses.Contains(v.Status)) e["status"] = "Choose a status from the list.";
        if (v.Condition is not null && !NonItFields.Conditions.Contains(v.Condition)) e["condition"] = "Choose a condition from the list.";
        if (v.Quantity is < 0) e["quantity"] = "Quantity cannot be negative.";
        if (v.PurchaseCost is < 0) e["purchaseCost"] = "Cost cannot be negative.";
        if (v.SalvageValue is < 0) e["salvageValue"] = "Salvage value cannot be negative.";
        if (v.SalvageValue is { } sv && v.PurchaseCost is { } pc && sv > pc) e["salvageValue"] = "Salvage value is more than the purchase cost.";
        if (v.UsefulLifeYears is < 0 or > 100) e["usefulLifeYears"] = "Enter a useful life between 0 and 100 years.";
        if (v.DepartmentId is { } d && lk.Departments.All(x => x.Id != d)) e["department"] = "This department no longer exists.";
        foreach (var f in NonItFields.All.Where(f => f.Type == NonItFieldType.Text))
            if (Get(v, f.Key) is string s && s.Length > f.MaxLength) e[f.Key] = $"{f.Label} is longer than {f.MaxLength} characters.";
        foreach (var (key, value) in v.Custom ?? [])
        {
            var f = lk.Fields.FirstOrDefault(x => x.Key == key);
            if (f is null || string.IsNullOrWhiteSpace(value)) continue;
            var ok = f.Type switch
            {
                "number" => SheetReader.ParseNumber(value) is not null,
                "date" => SheetReader.ParseDate(value) is not null,
                "yesno" => value.Trim().ToLowerInvariant() is "yes" or "no" or "true" or "false",
                "list" => f.Options.Length == 0 || f.Options.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase),
                _ => value.Length <= 500,
            };
            if (!ok) e["custom." + key] = $"{f.Label}: \"{value}\" is not a valid {(f.Type == "list" ? "choice" : f.Type)}.";
        }
        return e;
    }

    private static object? Get(NonItValues v, string key) => key switch
    {
        "assetTag" => v.AssetTag, "name" => v.Name, "manufacturer" => v.Manufacturer, "model" => v.Model, "serialNumber" => v.SerialNumber,
        "barcode" => v.Barcode, "location" => v.Location, "assignedTo" => v.AssignedTo, "vendor" => v.Vendor, "invoiceNumber" => v.InvoiceNumber,
        "poNumber" => v.PoNumber, "amcVendor" => v.AmcVendor, "notes" => v.Notes, _ => null,
    };

    private static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Creates (id null) or updates an asset and records the change. Returns the id.</summary>
    public static async Task<Guid> SaveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid? id, NonItValues v, NonItLookups lk, string? actor, string action, CancellationToken ct)
    {
        var errors = Validate(v, lk);
        if (errors.Count > 0) throw new NonItValidationException(errors);
        var tag = T(v.AssetTag);
        if (tag is null && id is null) tag = await NextTagAsync(conn, tx, ct);
        var barcode = T(v.Barcode);

        // Asset tag and barcode are unique (case-insensitive tag).
        await using (var dup = new NpgsqlCommand("""
            SELECT CASE WHEN lower(asset_tag) = lower(@t) THEN 'assetTag' ELSE 'barcode' END FROM zentriq.nonit_assets
             WHERE (@id::uuid IS NULL OR id <> @id) AND ((@t::text IS NOT NULL AND lower(asset_tag) = lower(@t)) OR (@b::text IS NOT NULL AND barcode = @b)) LIMIT 1
            """, conn, tx))
        {
            dup.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = (object?)id ?? DBNull.Value });
            dup.Parameters.Add(Text("t", tag));
            dup.Parameters.Add(Text("b", barcode));
            if (await dup.ExecuteScalarAsync(ct) is string which)
                throw new NonItValidationException(new() { [which] = which == "assetTag" ? $"Asset tag {tag} is already used by another asset." : $"Barcode {barcode} is already used by another asset." });
        }

        var custom = (v.Custom ?? []).Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && lk.Fields.Any(f => f.Key == kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value!.Trim());
        var sql = id is null
            ? """
              INSERT INTO zentriq.nonit_assets (id, asset_tag, barcode, name, category_id, manufacturer, model, serial_number, status, condition, quantity, location,
                  department_id, assigned_to, purchase_date, vendor, purchase_cost, invoice_number, po_number, warranty_until, amc_vendor, amc_until, insurance_until,
                  last_maintenance, next_maintenance, useful_life_years, salvage_value, disposed_on, notes, custom, created_by, updated_by)
              VALUES (@id, @tag, @bc, @name, @cat, @mf, @mo, @sn, @st, @co, @qty, @loc, @dep, @asg, @pd, @ven, @cost, @inv, @po, @wu, @amcv, @amcu, @insu,
                  @lm, @nm, @life, @salv, CASE WHEN @st = 'disposed' THEN current_date END, @notes, @custom, @actor, @actor)
              RETURNING NULL::jsonb, to_jsonb(nonit_assets.*)
              """
            : """
              WITH old AS (SELECT to_jsonb(a.*) AS j FROM zentriq.nonit_assets a WHERE a.id = @id FOR UPDATE)
              UPDATE zentriq.nonit_assets SET asset_tag = COALESCE(@tag, asset_tag), barcode = @bc, name = @name, category_id = @cat, manufacturer = @mf, model = @mo,
                  serial_number = @sn, status = @st, condition = @co, quantity = @qty, location = @loc, department_id = @dep, assigned_to = @asg,
                  purchase_date = @pd, vendor = @ven, purchase_cost = @cost, invoice_number = @inv, po_number = @po, warranty_until = @wu, amc_vendor = @amcv,
                  amc_until = @amcu, insurance_until = @insu, last_maintenance = @lm, next_maintenance = @nm, useful_life_years = @life, salvage_value = @salv,
                  disposed_on = CASE WHEN @st = 'disposed' THEN COALESCE(disposed_on, current_date) END,
                  notes = @notes, custom = @custom, updated_at = now(), updated_by = @actor, row_version = row_version + 1
               WHERE id = @id AND (@rv::int IS NULL OR row_version = @rv)
              RETURNING (SELECT j FROM old), to_jsonb(nonit_assets.*)
              """;
        id ??= Guid.NewGuid();
        string? before = null, after = null;
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            var p = cmd.Parameters;
            p.AddWithValue("id", id.Value);
            p.Add(Text("tag", tag)); p.Add(Text("bc", barcode)); p.Add(Text("name", T(v.Name)));
            p.Add(new NpgsqlParameter("cat", NpgsqlDbType.Uuid) { Value = (object?)v.CategoryId ?? DBNull.Value });
            p.Add(Text("mf", T(v.Manufacturer))); p.Add(Text("mo", T(v.Model))); p.Add(Text("sn", T(v.SerialNumber)));
            p.Add(Text("st", v.Status ?? "in_use")); p.Add(Text("co", v.Condition));
            p.AddWithValue("qty", v.Quantity ?? 1);
            p.Add(Text("loc", T(v.Location)));
            p.Add(new NpgsqlParameter("dep", NpgsqlDbType.Uuid) { Value = (object?)v.DepartmentId ?? DBNull.Value });
            p.Add(Text("asg", T(v.AssignedTo)));
            p.Add(Date("pd", v.PurchaseDate)); p.Add(Text("ven", T(v.Vendor))); p.Add(Num("cost", v.PurchaseCost));
            p.Add(Text("inv", T(v.InvoiceNumber))); p.Add(Text("po", T(v.PoNumber)));
            p.Add(Date("wu", v.WarrantyUntil)); p.Add(Text("amcv", T(v.AmcVendor))); p.Add(Date("amcu", v.AmcUntil)); p.Add(Date("insu", v.InsuranceUntil));
            p.Add(Date("lm", v.LastMaintenance)); p.Add(Date("nm", v.NextMaintenance)); p.Add(Num("life", v.UsefulLifeYears)); p.Add(Num("salv", v.SalvageValue));
            p.Add(Text("notes", T(v.Notes)));
            p.Add(new NpgsqlParameter("custom", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(custom) });
            p.Add(Text("actor", actor));
            p.Add(new NpgsqlParameter("rv", NpgsqlDbType.Integer) { Value = (object?)v.RowVersion ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                throw new NonItValidationException(new() { ["rowVersion"] = "Someone else changed this asset in the meantime. Reload it and try again." });
            before = r.IsDBNull(0) ? null : r.GetString(0);
            after = r.GetString(1);
        }
        await AddHistoryAsync(conn, tx, id.Value, actor, action, Diff(before, after), ct);
        return id.Value;
    }

    /// <summary>The stored values of an asset (for edits that change only some fields, such as an import update).</summary>
    public static async Task<NonItValues?> LoadValuesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("""
            SELECT asset_tag, barcode, name, category_id, manufacturer, model, serial_number, status, condition, quantity, location, department_id, assigned_to,
                   purchase_date, vendor, purchase_cost, invoice_number, po_number, warranty_until, amc_vendor, amc_until, insurance_until, last_maintenance,
                   next_maintenance, useful_life_years, salvage_value, notes, custom::text, row_version
              FROM zentriq.nonit_assets WHERE id = @id
            """, conn, tx);
        c.Parameters.AddWithValue("id", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        DateOnly? D(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);
        decimal? N(int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
        return new NonItValues
        {
            AssetTag = S(0), Barcode = S(1), Name = S(2), CategoryId = r.IsDBNull(3) ? null : r.GetGuid(3), Manufacturer = S(4), Model = S(5), SerialNumber = S(6),
            Status = S(7), Condition = S(8), Quantity = r.GetInt32(9), Location = S(10), DepartmentId = r.IsDBNull(11) ? null : r.GetGuid(11), AssignedTo = S(12),
            PurchaseDate = D(13), Vendor = S(14), PurchaseCost = N(15), InvoiceNumber = S(16), PoNumber = S(17), WarrantyUntil = D(18), AmcVendor = S(19),
            AmcUntil = D(20), InsuranceUntil = D(21), LastMaintenance = D(22), NextMaintenance = D(23), UsefulLifeYears = N(24), SalvageValue = N(25), Notes = S(26),
            Custom = JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(27)) ?? [], RowVersion = r.GetInt32(28),
        };
    }

    /// <summary>Changed fields: { field: { from, to } } (technical fields left out).</summary>
    public static Dictionary<string, object?> Diff(string? before, string? after)
    {
        var d = new Dictionary<string, object?>();
        if (before is null || after is null) return d;
        using var a = JsonDocument.Parse(before);
        using var b = JsonDocument.Parse(after);
        foreach (var p in b.RootElement.EnumerateObject())
        {
            if (p.Name is "updated_at" or "updated_by" or "row_version" or "created_at" or "created_by" or "id") continue;
            var old = a.RootElement.TryGetProperty(p.Name, out var o) ? o.GetRawText() : "null";
            if (old != p.Value.GetRawText())
                d[p.Name] = new { from = a.RootElement.TryGetProperty(p.Name, out var o2) ? Plain(o2) : null, to = Plain(p.Value) };
        }
        return d;
    }

    private static object? Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.True => true, JsonValueKind.False => false,
        _ => e.GetRawText(),
    };

    public static async Task AddHistoryAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, string? actor, string action, object changes, CancellationToken ct)
    {
        await using var h = new NpgsqlCommand("INSERT INTO zentriq.nonit_history (asset_id, actor, action, changes) VALUES (@a, @u, @x, @c)", conn, tx);
        h.Parameters.AddWithValue("a", id);
        h.Parameters.Add(Text("u", actor));
        h.Parameters.AddWithValue("x", action);
        h.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(changes) });
        await h.ExecuteNonQueryAsync(ct);
    }

    public static async Task<string> NextTagAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            await using var c = new NpgsqlCommand("""
                SELECT 'NIT-' || lpad(nextval('zentriq.nonit_tag_seq')::text, 6, '0') AS t
                """, conn, tx);
            var tag = (string)(await c.ExecuteScalarAsync(ct))!;
            await using var e = new NpgsqlCommand("SELECT 1 FROM zentriq.nonit_assets WHERE lower(asset_tag) = lower(@t)", conn, tx);
            e.Parameters.AddWithValue("t", tag);
            if (await e.ExecuteScalarAsync(ct) is null) return tag;
        }
        throw new InvalidOperationException("Could not find a free asset tag.");
    }

    // ---- Excel template ---------------------------------------------------------------------------------------------

    /// <summary>The bulk-upload form: "Assets" sheet (headings, drop-down lists), "Instructions" and the hidden "Lists" sheet.</summary>
    public static byte[] Template(NonItLookups lk)
    {
        var headings = Headings(lk);
        var assets = new XlsxSheet("Assets");
        assets.Headings.AddRange(headings.Select(h => h.Title));
        for (var i = 0; i < headings.Count; i++)
        {
            assets.Widths.Add(headings[i].Key is "name" or "notes" ? 32 : headings[i].Key is "category" or "location" ? 26 : 18);
            if (headings[i].Required) assets.Required.Add(i);
        }

        // Lists sheet: one column per drop-down list.
        var columns = new List<(string Title, List<string> Values, string? ForKey)>
        {
            ("Category", lk.Categories.Select(c => c.Name).ToList(), "category"),
            ("Category group", lk.Categories.Select(c => c.Group).ToList(), null),
            ("Status", NonItFields.Statuses.Select(NonItFields.StatusLabel).ToList(), "status"),
            ("Condition", NonItFields.Conditions.Select(NonItFields.ConditionLabel).ToList(), "condition"),
            ("Department", lk.Departments.Select(d => d.Name).ToList(), "department"),
        };
        foreach (var h in headings.Where(h => h.Options is { Length: > 0 })) columns.Add((h.Title, h.Options!.ToList(), h.Key));
        var lists = new XlsxSheet("Lists") { Hidden = false };
        lists.Headings.AddRange(columns.Select(c => c.Title));
        lists.Widths.AddRange(columns.Select(_ => 28.0));
        var depth = columns.Max(c => c.Values.Count);
        for (var r = 0; r < depth; r++) lists.Rows.Add(columns.Select(c => r < c.Values.Count ? (object?)c.Values[r] : null).ToArray());
        for (var c = 0; c < columns.Count; c++)
        {
            if (columns[c].ForKey is not { } key || columns[c].Values.Count == 0) continue;
            var idx = headings.FindIndex(h => h.Key == key);
            var col = XlsxBook.Col(c);
            if (idx >= 0) assets.DropDowns[idx] = $"Lists!${col}$2:${col}${columns[c].Values.Count + 1}";
        }

        var help = new XlsxSheet("Instructions") { FreezeHeader = false };
        help.Headings.AddRange(["Column", "Required", "What to enter"]);
        help.Widths.AddRange([26, 10, 110]);
        help.Rows.Add(["How to use this form", null, "Fill one row per item on the Assets sheet (keep the headings), save, then upload the file in Zentriq: Non-IT Asset Inventory, Bulk upload. A row with an existing asset tag updates that asset."]);
        help.Rows.Add(["Dates", null, "Excel dates, or text as YYYY-MM-DD or DD/MM/YYYY."]);
        help.Rows.Add(["Lists", null, "Category, status, condition and department offer the values on the Lists sheet. New categories can be added in Zentriq, or created by the upload when you choose so."]);
        help.Rows.Add([null, null, null]);
        foreach (var h in headings) help.Rows.Add([h.Title.TrimEnd(' ', '*'), h.Required ? "Yes" : "", h.Hint ?? ""]);
        return XlsxBook.Write(assets, help, lists);
    }

    public sealed record Heading(string Key, string Title, bool Required, string? Hint, string[]? Options, bool IsCustom);

    /// <summary>Template / export columns: the standard fields then the organisation's custom fields.</summary>
    public static List<Heading> Headings(NonItLookups lk) =>
        NonItFields.All.Select(f => new Heading(f.Key, f.Required ? f.Label + " *" : f.Label, f.Required, f.Hint, null, false))
            .Concat(lk.Fields.Select(f => new Heading("custom." + f.Key, f.Label, false,
                $"Custom field ({f.Type}){(f.Options.Length > 0 ? ": " + string.Join(", ", f.Options) : "")}", f.Type == "list" ? f.Options : null, true)))
            .ToList();

    private static NpgsqlParameter Text(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = (object?)v ?? DBNull.Value };
    private static NpgsqlParameter Date(string n, DateOnly? v) => new(n, NpgsqlDbType.Date) { Value = v.HasValue ? v.Value : DBNull.Value };
    private static NpgsqlParameter Num(string n, decimal? v) => new(n, NpgsqlDbType.Numeric) { Value = v.HasValue ? v.Value : DBNull.Value };

    public static string Invariant(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}
