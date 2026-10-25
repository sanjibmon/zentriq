using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.NonIt;

namespace Zentriq.Infrastructure.Lifecycle;

public sealed record LcImportOptions(bool DryRun = true, bool UpdateExisting = true, bool CreateMissing = true);
public sealed record LcImportError(int Row, string Message);
public sealed record LcColumn(string Key, string Title, bool Required, string Hint, string? List = null);

public sealed class LcImportResult
{
    public string Kind { get; init; } = "";
    public int Rows { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<LcImportError> Errors { get; } = [];
    public List<string> NewDepartments { get; } = [];
    public List<string> NewLocations { get; } = [];
    public List<string> NewPeople { get; } = [];
    public List<string> UnknownColumns { get; } = [];
    public bool DryRun { get; init; }
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Bulk upload for Asset Lifecycle: IT assets, people and contracts from the Excel template (or any .xlsx / .csv with the
/// same headings). A check (dry run) reports every problem by spreadsheet row; the upload applies all rows in one
/// transaction — all or nothing. IT assets are matched by asset tag, then serial number: existing ones are updated (only
/// the filled cells change), others are added to the register like "Add new assets".
/// </summary>
public static class LifecycleImport
{
    public static readonly string[] Kinds = ["assets", "people", "contracts"];

    public static readonly string[] Acquisitions = ["purchase", "lease", "rental", "donation", "byod"];
    public static readonly string[] ContractKinds = ["warranty", "amc", "support", "lease", "insurance", "other"];
    private static readonly string[] NewStages = ["planned", "on_order", "in_stock", "in_use", "in_maintenance"];

    public static string Title(string kind) => kind switch { "people" => "people", "contracts" => "contracts", _ => "IT assets" };

    public static List<LcColumn> Columns(string kind, IReadOnlyList<LcField> fields) => kind switch
    {
        "people" =>
        [
            new("name", "Name *", true, "Full name, as used when assigning assets. A row with a name, employee ID or e-mail already in the directory updates that person."),
            new("email", "E-mail", false, "Work e-mail."),
            new("employeeId", "Employee ID", false, "Your HR / employee number."),
            new("title", "Job title", false, ""),
            new("phone", "Phone", false, ""),
            new("department", "Department", false, "A Zentriq department (name or code). New ones are created when you choose so.", "Department"),
            new("location", "Location", false, "A Zentriq location (name or code). New ones are created when you choose so.", "Location"),
            new("manager", "Manager", false, "Name of the person's manager."),
            new("status", "Status", false, "Active (default) or Left (has left the organisation).", "Person status"),
            new("notes", "Notes", false, ""),
        ],
        "contracts" =>
        [
            new("name", "Contract name *", true, "A contract with the same name is updated."),
            new("kind", "Type *", true, "Warranty, AMC, Support, Lease, Insurance or Other.", "Contract type"),
            new("vendor", "Vendor", false, ""),
            new("reference", "Contract number", false, ""),
            new("startDate", "Start date", false, "YYYY-MM-DD, DD/MM/YYYY or an Excel date."),
            new("endDate", "End date", false, "Reminders (Integrations) are raised before and on this date."),
            new("cost", "Cost", false, "Amount, for example 125000 or 1,25,000.00."),
            new("currency", "Currency", false, "Three letters, for example INR."),
            new("notifyDays", "Remind days before end", false, "0 to 365 (default 90)."),
            new("assets", "Asset tags", false, "Asset tags this contract covers, separated by commas (they are added to the contract)."),
            new("notes", "Notes", false, ""),
        ],
        _ => new List<LcColumn>
        {
            new("assetTag", "Asset tag", false, "Your own tag (letters, digits and . _ / -). Empty: the next tag in your format when tags are automatic. A tag already in Zentriq updates that asset."),
            new("serial", "Serial number *", false, "Serial number of the device. Needed for new devices (or a host name). Zentriq links the row to the device when an agent or scan finds the same serial."),
            new("hostname", "Host name", false, "Computer name, if known."),
            new("manufacturer", "Manufacturer", false, "For example Dell Inc., Lenovo, HP."),
            new("model", "Model", false, ""),
            new("deviceType", "Device type", false, "Laptop, Desktop, Server … (see the Lists sheet). New devices without one are Laptops.", "Device type"),
            new("stage", "Stage", false, "Planned, On order, In stock, In use or In maintenance for new devices (default In stock). For existing ones, any allowed change.", "Stage"),
            new("status", "Status", false, "A status of the stage (see Lists); empty = the usual one.", "Status"),
            new("assignedTo", "Assigned to", false, "Person the asset is assigned to (makes it In use · Assigned). New names are added to People."),
            new("assignedEmail", "Assigned e-mail", false, ""),
            new("expectedReturn", "Loan ends", false, "For status On loan."),
            new("department", "Department", false, "A Zentriq department (name or code). New ones are created when you choose so.", "Department"),
            new("location", "Location", false, "A Zentriq location (name or code). New ones are created when you choose so.", "Location"),
            new("acquisition", "Acquisition", false, "Purchase, Lease, Rental, Donation or BYOD.", "Acquisition"),
            new("purchaseDate", "Purchase date", false, "YYYY-MM-DD, DD/MM/YYYY or an Excel date."),
            new("purchaseCost", "Purchase cost", false, "Amount, for example 98500."),
            new("currency", "Currency", false, "Three letters (default: the lifecycle currency)."),
            new("vendor", "Vendor", false, ""),
            new("invoiceNumber", "Invoice number", false, ""),
            new("poNumber", "PO number", false, ""),
            new("depreciationMethod", "Depreciation method", false, "Straight-line, Declining balance or Not depreciated; empty = the class policy.", "Depreciation"),
            new("usefulLifeMonths", "Useful life (months)", false, "Empty = the class policy."),
            new("warrantyProvider", "Warranty provider", false, ""),
            new("warrantyType", "Warranty type", false, "For example ProSupport NBD onsite."),
            new("warrantyStart", "Warranty start", false, ""),
            new("warrantyEnd", "Warranty end", false, ""),
            new("lessor", "Lessor", false, "For leased assets."),
            new("leaseEnd", "Lease end", false, ""),
            new("leaseMonthlyCost", "Lease monthly cost", false, ""),
            new("notes", "Notes", false, ""),
        }.Concat(fields.Select(f => new LcColumn("custom." + f.Key, f.Label + (f.Required ? " *" : ""), f.Required,
            $"Custom field ({TypeLabel(f.Type)}){(f.Options.Length > 0 ? ": " + string.Join(", ", f.Options) : "")}",
            f.Type == "list" ? "custom." + f.Key : f.Type == "yesno" ? "Yes / No" : null))).ToList(),
    };

    private static string TypeLabel(string t) => t switch { "number" => "number", "date" => "date", "list" => "list", "yesno" => "yes / no", _ => "text" };
    private static string Norm(string s) => NonItFields.NormalizeHeading(s);

    // ---------------------------------------------------------------------------------------------------------------
    // Lookups

    private sealed class Lookups
    {
        public Dictionary<string, Guid> Departments { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Guid> Locations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DepartmentNames { get; } = [];
        public List<string> LocationNames { get; } = [];
        public List<string> People { get; } = [];
        public List<LcField> Fields { get; set; } = [];
        public string Currency { get; set; } = "INR";
    }

    private static async Task<Lookups> LoadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var lk = new Lookups();
        await using (var d = new NpgsqlCommand("SELECT id, name, code FROM zentriq.departments ORDER BY lower(name)", conn, tx))
        await using (var r = await d.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                lk.Departments[r.GetString(1).Trim()] = r.GetGuid(0);
                if (!r.IsDBNull(2) && r.GetString(2).Trim().Length > 0) lk.Departments.TryAdd(r.GetString(2).Trim(), r.GetGuid(0));
                lk.DepartmentNames.Add(r.GetString(1));
            }
        await using (var l = new NpgsqlCommand("SELECT id, name, code FROM zentriq.locations ORDER BY lower(name)", conn, tx))
        await using (var r = await l.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                lk.Locations[r.GetString(1).Trim()] = r.GetGuid(0);
                if (!r.IsDBNull(2) && r.GetString(2).Trim().Length > 0) lk.Locations.TryAdd(r.GetString(2).Trim(), r.GetGuid(0));
                lk.LocationNames.Add(r.GetString(1));
            }
        await using (var p = new NpgsqlCommand("SELECT name FROM zentriq.lifecycle_people WHERE status = 'active' ORDER BY lower(name) LIMIT 5000", conn, tx))
        await using (var r = await p.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) lk.People.Add(r.GetString(0));
        await using (var c = new NpgsqlCommand("SELECT COALESCE((SELECT value #>> '{}' FROM zentriq.system_settings WHERE key = 'lifecycle.currency'), 'INR')", conn, tx))
            lk.Currency = (string)(await c.ExecuteScalarAsync(ct))!;
        lk.Fields = await LifecycleFields.LoadAsync(conn, tx, ct);
        return lk;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Template

    public static async Task<byte[]> TemplateAsync(NpgsqlConnection conn, string kind, CancellationToken ct)
    {
        var lk = await LoadAsync(conn, null, ct);
        var cols = Columns(kind, lk.Fields);
        var sheetName = kind switch { "people" => "People", "contracts" => "Contracts", _ => "Assets" };
        var data = new XlsxSheet(sheetName);
        data.Headings.AddRange(cols.Select(c => c.Title));
        for (var i = 0; i < cols.Count; i++)
        {
            data.Widths.Add(cols[i].Key is "notes" or "name" or "assets" ? 30 : cols[i].Key is "model" or "assignedTo" or "vendor" or "email" ? 24 : 18);
            if (cols[i].Required) data.Required.Add(i);
        }
        data.Rows.Add(Example(kind, cols));

        var lists = new List<(string Title, List<string> Values)>
        {
            ("Device type", Zentriq.Inventory.DeviceTypes.All.Select(t => t.Label).ToList()),
            ("Stage", LifecycleStore.Stages.Select(s => s.Label).ToList()),
            ("Status", LifecycleStore.Stages.SelectMany(s => s.Statuses).Distinct().Select(LifecycleStore.StatusLabel).ToList()),
            ("Department", lk.DepartmentNames),
            ("Location", lk.LocationNames),
            ("Acquisition", ["Purchase", "Lease", "Rental", "Donation", "BYOD"]),
            ("Depreciation", ["Straight-line", "Declining balance", "Not depreciated"]),
            ("Person status", ["Active", "Left"]),
            ("Contract type", ["Warranty", "AMC", "Support", "Lease", "Insurance", "Other"]),
            ("Yes / No", ["Yes", "No"]),
            ("People", lk.People),
        };
        foreach (var f in lk.Fields.Where(f => f.Type == "list")) lists.Add(("custom." + f.Key, f.Options.ToList()));
        var listSheet = new XlsxSheet("Lists");
        listSheet.Headings.AddRange(lists.Select(l => l.Title.StartsWith("custom.", StringComparison.Ordinal) ? lk.Fields.First(f => "custom." + f.Key == l.Title).Label : l.Title));
        listSheet.Widths.AddRange(lists.Select(_ => 26.0));
        var depth = Math.Max(1, lists.Max(l => l.Values.Count));
        for (var r = 0; r < depth; r++) listSheet.Rows.Add(lists.Select(l => r < l.Values.Count ? (object?)l.Values[r] : null).ToArray());
        for (var i = 0; i < cols.Count; i++)
        {
            if (cols[i].List is not { } list) continue;
            var idx = lists.FindIndex(l => l.Title == list);
            if (idx < 0 || lists[idx].Values.Count == 0) continue;
            var col = XlsxBook.Col(idx);
            data.DropDowns[i] = $"Lists!${col}$2:${col}${lists[idx].Values.Count + 1}";
        }

        var help = new XlsxSheet("Instructions") { FreezeHeader = false };
        help.Headings.AddRange(["Column", "Required", "What to enter"]);
        help.Widths.AddRange([28, 10, 110]);
        help.Rows.Add(["How to use this form", null, kind switch
        {
            "people" => "One row per person on the People sheet (keep the headings; delete the example row), save, then upload in Zentriq: Asset Lifecycle › People › Bulk upload.",
            "contracts" => "One row per contract on the Contracts sheet (keep the headings; delete the example row), save, then upload in Zentriq: Asset Lifecycle › Contracts › Bulk upload.",
            _ => "One row per IT asset on the Assets sheet (keep the headings; delete the example row), save, then upload in Zentriq: Asset Lifecycle › Bulk upload. Check the file first: nothing is saved until every row is correct.",
        }]);
        if (kind == "assets")
        {
            help.Rows.Add(["Existing assets", null, "A row whose asset tag or serial number is already in Zentriq updates that asset: only the filled cells change. A different stage or person is a lifecycle change (recorded in its history)."]);
            help.Rows.Add(["New assets", null, "Other rows are added to the register (default In stock). When an agent or a scan finds the same serial number, the asset becomes In use by itself."]);
        }
        help.Rows.Add(["Dates", null, "Excel dates, or text as YYYY-MM-DD or DD/MM/YYYY."]);
        help.Rows.Add(["Lists", null, "Columns with a list offer the values on the Lists sheet. Department, location and people that do not exist yet can be created by the upload."]);
        help.Rows.Add([null, null, null]);
        foreach (var c in cols) help.Rows.Add([c.Title.TrimEnd(' ', '*'), c.Required ? "Yes" : "", c.Hint]);
        return XlsxBook.Write(data, help, listSheet);
    }

    private static object?[] Example(string kind, List<LcColumn> cols)
    {
        var ex = kind switch
        {
            "people" => new Dictionary<string, object?> { ["name"] = "Asha Rao", ["email"] = "asha.rao@example.com", ["employeeId"] = "E1024", ["title"] = "Accountant", ["department"] = "Finance", ["status"] = "Active" },
            "contracts" => new Dictionary<string, object?> { ["name"] = "Dell ProSupport 2026 — laptops", ["kind"] = "Support", ["vendor"] = "Dell", ["reference"] = "PS-88812", ["startDate"] = new DateOnly(2026, 4, 1), ["endDate"] = new DateOnly(2029, 3, 31), ["cost"] = 1450000m, ["currency"] = "INR", ["notifyDays"] = 90m },
            _ => new Dictionary<string, object?>
            {
                ["assetTag"] = "", ["serial"] = "5CG4410XYZ", ["manufacturer"] = "HP", ["model"] = "EliteBook 840 G11", ["deviceType"] = "Laptop", ["stage"] = "In stock",
                ["purchaseDate"] = new DateOnly(2026, 10, 15), ["purchaseCost"] = 112000m, ["vendor"] = "HP India", ["poNumber"] = "PO-2026-118", ["warrantyEnd"] = new DateOnly(2029, 10, 14),
            },
        };
        return cols.Select(c => ex.GetValueOrDefault(c.Key)).ToArray();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Upload

    public static async Task<LcImportResult> RunAsync(NpgsqlConnection conn, string kind, List<string[]> sheet, LcImportOptions o, string actor, CancellationToken ct)
    {
        var result = new LcImportResult { Kind = kind, DryRun = o.DryRun };
        await using var tx = await conn.BeginTransactionAsync(ct);
        var lk = await LoadAsync(conn, tx, ct);
        var cols = Columns(kind, lk.Fields);
        var key = cols[kind == "assets" ? 1 : 0];   // serial (assets) or name (people, contracts) identifies the heading row
        var headerRow = sheet.FindIndex(r => r.Any(c => Norm(c) == Norm(key.Title)) || (kind == "assets" && r.Any(c => Norm(c) == "assettag")));
        if (headerRow < 0)
        {
            result.Errors.Add(new(1, $"No heading row found. Download the template and keep its headings ({key.Title.TrimEnd(' ', '*')} …)."));
            return result;
        }
        var header = sheet[headerRow];
        var map = new Dictionary<int, LcColumn>();
        for (var c = 0; c < header.Length; c++)
        {
            var h = Norm(header[c]);
            if (h.Length == 0) continue;
            var match = cols.FirstOrDefault(x => Norm(x.Title) == h || Norm(x.Key.Replace("custom.", "")) == h);
            if (match is null) result.UnknownColumns.Add(header[c].Trim());
            else map.TryAdd(c, match);
        }
        foreach (var req in cols.Where(c => c.Required && !c.Key.StartsWith("custom.", StringComparison.Ordinal)))
            if (!map.ContainsValue(req)) { result.Errors.Add(new(headerRow + 1, $"The column {req.Title.TrimEnd(' ', '*')} is missing.")); return result; }

        var ctx = new RowContext(conn, tx, lk, o, actor, result, ct);
        var tags = await TagSettings.LoadAsync(conn, tx, ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        for (var i = headerRow + 1; i < sheet.Count; i++)
        {
            var row = sheet[i];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (++n > SheetReader.MaxRows) { result.Errors.Add(new(i + 1, $"At most {SheetReader.MaxRows} rows per upload.")); break; }
            result.Rows++;
            var cells = new Dictionary<string, string>();
            foreach (var (c, col) in map) if (c < row.Length && !string.IsNullOrWhiteSpace(row[c])) cells[col.Key] = row[c].Trim();
            await using (var sp = new NpgsqlCommand("SAVEPOINT lc_row", conn, tx)) await sp.ExecuteNonQueryAsync(ct);
            try
            {
                switch (kind)
                {
                    case "people": await PersonRowAsync(ctx, cells, seen); break;
                    case "contracts": await ContractRowAsync(ctx, cells, seen); break;
                    default: await AssetRowAsync(ctx, cells, seen, tags); break;
                }
                await using var rel = new NpgsqlCommand("RELEASE SAVEPOINT lc_row", conn, tx);
                await rel.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex) when (ex is LifecycleValidationException or RowException or PostgresException)
            {
                await using (var rb = new NpgsqlCommand("ROLLBACK TO SAVEPOINT lc_row", conn, tx)) await rb.ExecuteNonQueryAsync(ct);
                result.Errors.Add(new(i + 1, ex is PostgresException pg ? Explain(pg) : ex.Message));
                if (result.Errors.Count >= 500) { result.Errors.Add(new(i + 1, "Stopped after 500 problems.")); break; }
            }
        }
        if (o.DryRun || !result.Ok) await tx.RollbackAsync(ct);
        else await tx.CommitAsync(ct);
        return result;
    }

    private static string Explain(PostgresException ex) => ex.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => "Duplicate value: " + (ex.ConstraintName switch
        {
            "ux_lifecycle_people_email" => "another person has this e-mail.",
            "ux_lifecycle_people_empid" => "another person has this employee ID.",
            "ux_lifecycle_people_name" => "another person has this name.",
            "ux_asset_lifecycle_tag" => "another asset has this asset tag.",
            _ => ex.MessageText,
        }),
        PostgresErrorCodes.CheckViolation => "A value is too long or not allowed (" + ex.ConstraintName + ").",
        _ => ex.MessageText,
    };

    private sealed class RowException(string message) : Exception(message);

    private sealed record RowContext(NpgsqlConnection Conn, NpgsqlTransaction Tx, Lookups Lk, LcImportOptions O, string Actor, LcImportResult Result, CancellationToken Ct);

    private static string? Get(Dictionary<string, string> c, string k, int max = 200) =>
        c.TryGetValue(k, out var v) ? (v.Length > max ? throw new RowException($"{k}: at most {max} characters.") : v) : null;
    private static DateOnly? Date(Dictionary<string, string> c, string k, string label) =>
        c.TryGetValue(k, out var v) ? SheetReader.ParseDate(v) ?? throw new RowException($"{label}: \"{v}\" is not a date (YYYY-MM-DD or DD/MM/YYYY).") : null;
    private static decimal? Num(Dictionary<string, string> c, string k, string label, decimal min = 0)
    {
        if (!c.TryGetValue(k, out var v)) return null;
        var d = SheetReader.ParseNumber(v) ?? throw new RowException($"{label}: \"{v}\" is not a number.");
        return d < min ? throw new RowException($"{label} cannot be below {min}.") : d;
    }
    private static NpgsqlParameter T(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = (object?)v ?? DBNull.Value };
    private static NpgsqlParameter D(string n, DateOnly? v) => new(n, NpgsqlDbType.Date) { Value = v.HasValue ? v.Value : DBNull.Value };
    private static NpgsqlParameter N(string n, decimal? v) => new(n, NpgsqlDbType.Numeric) { Value = v.HasValue ? v.Value : DBNull.Value };
    private static NpgsqlParameter U(string n, Guid? v) => new(n, NpgsqlDbType.Uuid) { Value = v.HasValue ? v.Value : DBNull.Value };

    private static async Task<Guid?> DepartmentAsync(RowContext x, string? name)
    {
        if (name is null) return null;
        if (x.Lk.Departments.TryGetValue(name, out var id)) return id;
        if (!x.O.CreateMissing) throw new RowException($"Department \"{name}\" does not exist (add it, or choose to create missing departments and locations).");
        id = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.departments (id, organization_id, name) VALUES (@id, (SELECT id FROM zentriq.organizations ORDER BY created_at LIMIT 1), @n)
            """, x.Conn, x.Tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("n", name.Length > 100 ? name[..100] : name);
        await cmd.ExecuteNonQueryAsync(x.Ct);
        x.Lk.Departments[name] = id;
        x.Result.NewDepartments.Add(name);
        return id;
    }

    private static async Task<Guid?> LocationAsync(RowContext x, string? name)
    {
        if (name is null) return null;
        if (x.Lk.Locations.TryGetValue(name, out var id)) return id;
        if (!x.O.CreateMissing) throw new RowException($"Location \"{name}\" does not exist (add it, or choose to create missing departments and locations).");
        id = Guid.NewGuid();
        await using var cmd = new NpgsqlCommand("INSERT INTO zentriq.locations (id, name) VALUES (@id, @n)", x.Conn, x.Tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("n", name.Length > 100 ? name[..100] : name);
        await cmd.ExecuteNonQueryAsync(x.Ct);
        x.Lk.Locations[name] = id;
        x.Result.NewLocations.Add(name);
        return id;
    }

    private static string? Pick(string? text, IEnumerable<(string Key, string Label)> options, string what)
    {
        if (text is null) return null;
        var t = Norm(text);
        foreach (var (k, l) in options) if (Norm(k) == t || Norm(l) == t) return k;
        throw new RowException($"{what}: \"{text}\" is not one of {string.Join(", ", options.Select(o => o.Label))}.");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // IT assets

    private static async Task AssetRowAsync(RowContext x, Dictionary<string, string> c, HashSet<string> seen, TagSettings tags)
    {
        var tag = Get(c, "assetTag", 40);
        var serial = Get(c, "serial", 100);
        var host = Get(c, "hostname", 63);
        if (tag is not null && !TagFormat.TagPattern.IsMatch(tag)) throw new RowException($"Asset tag \"{tag}\": use letters, digits and . _ / - (up to 40 characters).");
        if (tag is not null && !seen.Add("tag:" + tag)) throw new RowException($"Asset tag {tag} appears more than once in the file.");
        if (serial is not null && !seen.Add("sn:" + serial)) throw new RowException($"Serial number {serial} appears more than once in the file.");

        var deviceType = Pick(Get(c, "deviceType"), Zentriq.Inventory.DeviceTypes.All.Select(t => (t.Key, t.Label)), "Device type");
        var stage = Pick(Get(c, "stage"), LifecycleStore.Stages.Select(s => (s.Stage, s.Label)), "Stage");
        var status = Get(c, "status") is { } st
            ? Pick(st, LifecycleStore.Stages.SelectMany(s => s.Statuses).Distinct().Select(s => (s, LifecycleStore.StatusLabel(s))), "Status") : null;
        var acquisition = Pick(Get(c, "acquisition"), [("purchase", "Purchase"), ("lease", "Lease"), ("rental", "Rental"), ("donation", "Donation"), ("byod", "BYOD")], "Acquisition");
        var depMethod = Pick(Get(c, "depreciationMethod"), [("straight_line", "Straight-line"), ("declining_balance", "Declining balance"), ("none", "Not depreciated")], "Depreciation method");
        var person = Get(c, "assignedTo");
        var email = Get(c, "assignedEmail", 254);
        var loanEnds = Date(c, "expectedReturn", "Loan ends");
        var purchaseDate = Date(c, "purchaseDate", "Purchase date");
        var cost = Num(c, "purchaseCost", "Purchase cost");
        var currency = Get(c, "currency", 3)?.ToUpperInvariant();
        if (currency is not null && !System.Text.RegularExpressions.Regex.IsMatch(currency, "^[A-Z]{3}$")) throw new RowException($"Currency \"{currency}\": use three letters, for example INR.");
        var life = Num(c, "usefulLifeMonths", "Useful life (months)");
        if (life is > 600) throw new RowException("Useful life is at most 600 months.");
        var wStart = Date(c, "warrantyStart", "Warranty start");
        var wEnd = Date(c, "warrantyEnd", "Warranty end");
        if (wStart is not null && wEnd is not null && wEnd < wStart) throw new RowException("The warranty ends before it starts.");
        var leaseEnd = Date(c, "leaseEnd", "Lease end");
        var leaseCost = Num(c, "leaseMonthlyCost", "Lease monthly cost");
        var custom = new JsonObject();
        foreach (var f in x.Lk.Fields)
        {
            c.TryGetValue("custom." + f.Key, out var v);
            if (v is null && !f.Required) continue;
            custom[f.Key] = LifecycleFields.Parse(f, v);
        }
        var depId = await DepartmentAsync(x, Get(c, "department", 100));
        var locId = await LocationAsync(x, Get(c, "location", 100));

        // Find the asset: tag first, then serial number.
        Guid? id = null;
        if (tag is not null)
        {
            await using var f = new NpgsqlCommand("SELECT asset_id FROM zentriq.asset_lifecycle WHERE lower(asset_tag) = lower(@t)", x.Conn, x.Tx);
            f.Parameters.AddWithValue("t", tag);
            id = await f.ExecuteScalarAsync(x.Ct) as Guid?;
        }
        if (id is null && serial is not null)
        {
            await using var f = new NpgsqlCommand("""
                SELECT a.id FROM zentriq.assets a LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
                 WHERE a.status IN ('active','retired') AND (upper(a.serial_number) = upper(@s) OR upper(h.serial_number) = upper(@s) OR upper(a.manual_serial) = upper(@s))
                 ORDER BY a.status, a.last_seen_at DESC NULLS LAST LIMIT 1
                """, x.Conn, x.Tx);
            f.Parameters.AddWithValue("s", serial);
            id = await f.ExecuteScalarAsync(x.Ct) as Guid?;
        }

        if (id is { } existing)
        {
            if (!x.O.UpdateExisting) { x.Result.Skipped++; return; }
            // Tag: a row that found the asset by serial may give it its tag.
            if (tag is not null)
            {
                await using var cur = new NpgsqlCommand("SELECT asset_tag FROM zentriq.asset_lifecycle WHERE asset_id = @a", x.Conn, x.Tx);
                cur.Parameters.AddWithValue("a", existing);
                if (!string.Equals(await cur.ExecuteScalarAsync(x.Ct) as string, tag, StringComparison.OrdinalIgnoreCase))
                    await LifecycleStore.SetTagAsync(x.Conn, x.Tx, existing, tag, x.Ct);
            }
            await DetailsAsync(x, existing, acquisition, purchaseDate, cost, currency, Get(c, "vendor"), Get(c, "invoiceNumber", 100), Get(c, "poNumber", 100), depMethod, (int?)life,
                Get(c, "warrantyProvider"), Get(c, "warrantyType", 100), wStart, wEnd, Get(c, "lessor"), leaseEnd, leaseCost, Get(c, "notes", 4000), custom);
            await AssetFieldsAsync(x, existing, host, Get(c, "manufacturer"), Get(c, "model"), deviceType, depId, locId);
            var now = await LifecycleStore.LockAsync(x.Conn, x.Tx, existing, x.Ct) ?? throw new RowException("The asset could not be read.");
            var wantStage = stage ?? (person is not null ? "in_use" : null);
            var personChanges = person is not null && !string.Equals(person, now.AssignedTo, StringComparison.OrdinalIgnoreCase);
            if (wantStage is not null && (wantStage != now.Stage || (status is not null && status != now.Status) || personChanges))
                await LifecycleStore.ChangeAsync(x.Conn, x.Tx, existing, new StageChange(wantStage, status, "Bulk upload", person, email, loanEnds), x.Actor, x.Ct);
            if (tag is null && tags.Auto) await LifecycleStore.EnsureTagAsync(x.Conn, x.Tx, existing, x.Ct, tags);
            x.Result.Updated++;
            return;
        }

        // A new IT asset in the register.
        if (serial is null && host is null) throw new RowException("Enter a serial number (or a host name) for a new asset.");
        var newStage = stage ?? (person is not null ? "in_use" : "in_stock");
        if (!NewStages.Contains(newStage)) throw new RowException($"New assets start as Planned, On order, In stock, In use or In maintenance (not {LifecycleStore.StageLabel(newStage)}).");
        var newStatus = status ?? LifecycleStore.DefaultStatus(newStage, person);
        if (!LifecycleStore.Stages.First(s => s.Stage == newStage).Statuses.Contains(newStatus))
            throw new RowException($"Status {LifecycleStore.StatusLabel(newStatus)} is not a status of {LifecycleStore.StageLabel(newStage)}.");
        if (newStatus is "assigned" or "loaned" && person is null) throw new RowException("Enter the person (Assigned to) for an assigned or loaned asset.");
        if (newStatus == "loaned" && loanEnds is null) throw new RowException("Enter the date the loan ends (Loan ends).");
        var newId = Guid.NewGuid();
        var type = deviceType ?? "laptop";
        await using (var ins = new NpgsqlCommand("""
            INSERT INTO zentriq.assets (id, hostname, serial_number, manufacturer, model, category, device_type, device_type_manual, source, department_id, location_id)
            VALUES (@id, @h, @sn, @mf, @md, @cat, @t, true, 'lifecycle', @dep, @loc)
            """, x.Conn, x.Tx))
        {
            ins.Parameters.AddWithValue("id", newId);
            ins.Parameters.Add(T("h", host));
            ins.Parameters.Add(T("sn", serial));
            ins.Parameters.Add(T("mf", Get(c, "manufacturer")));
            ins.Parameters.Add(T("md", Get(c, "model")));
            ins.Parameters.AddWithValue("cat", Zentriq.Inventory.DeviceTypes.Get(type)?.Category ?? "endpoint");
            ins.Parameters.AddWithValue("t", type);
            ins.Parameters.Add(U("dep", depId));
            ins.Parameters.Add(U("loc", locId));
            await ins.ExecuteNonQueryAsync(x.Ct);
        }
        var stockLike = newStage is "planned" or "on_order" or "in_stock";
        await using (var upd = new NpgsqlCommand("""
            UPDATE zentriq.asset_lifecycle SET stage = @stage, status = @status, auto_deploy = @auto, stage_reason = 'Bulk upload',
                   in_service_date = CASE WHEN @stage = 'in_use' THEN current_date END,
                   assigned_to = @p, assigned_email = @e, assigned_at = CASE WHEN @p::text IS NOT NULL THEN now() END,
                   expected_return = CASE WHEN @status = 'loaned' THEN @ret END, updated_by = @by
             WHERE asset_id = @id
            """, x.Conn, x.Tx))
        {
            upd.Parameters.AddWithValue("id", newId);
            upd.Parameters.AddWithValue("stage", newStage);
            upd.Parameters.AddWithValue("status", newStatus);
            upd.Parameters.AddWithValue("auto", stockLike);
            upd.Parameters.Add(T("p", newStage == "in_use" ? person : null));
            upd.Parameters.Add(T("e", newStage == "in_use" ? email : null));
            upd.Parameters.Add(D("ret", loanEnds));
            upd.Parameters.Add(T("by", x.Actor));
            await upd.ExecuteNonQueryAsync(x.Ct);
        }
        await DetailsAsync(x, newId, acquisition, purchaseDate, cost, currency, Get(c, "vendor"), Get(c, "invoiceNumber", 100), Get(c, "poNumber", 100), depMethod, (int?)life,
            Get(c, "warrantyProvider"), Get(c, "warrantyType", 100), wStart, wEnd, Get(c, "lessor"), leaseEnd, leaseCost, Get(c, "notes", 4000), custom);
        if (person is not null && newStage == "in_use")
        {
            if (await LifecycleStore.AddPersonAsync(x.Conn, x.Tx, person, email, "import", x.Actor, x.Ct)) x.Result.NewPeople.Add(person);
            await using var own = new NpgsqlCommand("UPDATE zentriq.assets SET owner_name = @p WHERE id = @id", x.Conn, x.Tx);
            own.Parameters.AddWithValue("p", person);
            own.Parameters.AddWithValue("id", newId);
            await own.ExecuteNonQueryAsync(x.Ct);
        }
        if (tag is not null) await LifecycleStore.SetTagAsync(x.Conn, x.Tx, newId, tag, x.Ct);
        else if (tags.Auto) await LifecycleStore.EnsureTagAsync(x.Conn, x.Tx, newId, x.Ct, tags);
        await LifecycleStore.EventAsync(x.Conn, x.Tx, newId, x.Actor, "created", null, newStage, null, newStatus, person, "Bulk upload", new { serial, model = Get(c, "model") }, x.Ct);
        x.Result.Created++;
    }

    private static async Task DetailsAsync(RowContext x, Guid id, string? acq, DateOnly? pd, decimal? cost, string? cur, string? vendor, string? inv, string? po, string? dm, int? life,
        string? wp, string? wt, DateOnly? ws, DateOnly? we, string? lessor, DateOnly? le, decimal? lmc, string? notes, JsonObject custom)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE zentriq.asset_lifecycle SET
                   acquisition = COALESCE(@acq, acquisition), purchase_date = COALESCE(@pd, purchase_date), purchase_cost = COALESCE(@cost, purchase_cost),
                   currency = COALESCE(@cur, currency), vendor = COALESCE(@vendor, vendor), invoice_number = COALESCE(@inv, invoice_number), po_number = COALESCE(@po, po_number),
                   depreciation_method = COALESCE(@dm, depreciation_method), useful_life_months = COALESCE(@life, useful_life_months),
                   warranty_provider = COALESCE(@wp, warranty_provider), warranty_type = COALESCE(@wt, warranty_type),
                   warranty_start = COALESCE(@ws, warranty_start, CASE WHEN @we::date IS NOT NULL THEN COALESCE(@pd, purchase_date) END), warranty_end = COALESCE(@we, warranty_end),
                   lessor = COALESCE(@lessor, lessor), lease_end = COALESCE(@le, lease_end), lease_monthly_cost = COALESCE(@lmc, lease_monthly_cost),
                   notes = COALESCE(@notes, notes), custom = custom || @custom, updated_at = now(), updated_by = @by, row_version = row_version + 1
             WHERE asset_id = @id
            """, x.Conn, x.Tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.Add(T("acq", acq));
        cmd.Parameters.Add(D("pd", pd));
        cmd.Parameters.Add(N("cost", cost));
        cmd.Parameters.Add(T("cur", cur));
        cmd.Parameters.Add(T("vendor", vendor));
        cmd.Parameters.Add(T("inv", inv));
        cmd.Parameters.Add(T("po", po));
        cmd.Parameters.Add(T("dm", dm));
        cmd.Parameters.Add(new NpgsqlParameter("life", NpgsqlDbType.Integer) { Value = (object?)life ?? DBNull.Value });
        cmd.Parameters.Add(T("wp", wp));
        cmd.Parameters.Add(T("wt", wt));
        cmd.Parameters.Add(D("ws", ws));
        cmd.Parameters.Add(D("we", we));
        cmd.Parameters.Add(T("lessor", lessor));
        cmd.Parameters.Add(D("le", le));
        cmd.Parameters.Add(N("lmc", lmc));
        cmd.Parameters.Add(T("notes", notes));
        cmd.Parameters.Add(new NpgsqlParameter("custom", NpgsqlDbType.Jsonb) { Value = custom.ToJsonString() });
        cmd.Parameters.Add(T("by", x.Actor));
        await cmd.ExecuteNonQueryAsync(x.Ct);
    }

    /// <summary>Host name, make, model and type only fill what the inventory does not know; department / location are set.</summary>
    private static async Task AssetFieldsAsync(RowContext x, Guid id, string? host, string? mf, string? model, string? type, Guid? dep, Guid? loc)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE zentriq.assets SET hostname = COALESCE(hostname, @h), manufacturer = COALESCE(manufacturer, @mf), model = COALESCE(model, @md),
                   device_type = CASE WHEN @t::text IS NOT NULL THEN @t ELSE device_type END,
                   device_type_manual = device_type_manual OR @t::text IS NOT NULL,
                   department_id = COALESCE(@dep, department_id), location_id = COALESCE(@loc, location_id), updated_at = now()
             WHERE id = @id
            """, x.Conn, x.Tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.Add(T("h", host));
        cmd.Parameters.Add(T("mf", mf));
        cmd.Parameters.Add(T("md", model));
        cmd.Parameters.Add(T("t", type));
        cmd.Parameters.Add(U("dep", dep));
        cmd.Parameters.Add(U("loc", loc));
        await cmd.ExecuteNonQueryAsync(x.Ct);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // People

    private static async Task PersonRowAsync(RowContext x, Dictionary<string, string> c, HashSet<string> seen)
    {
        var name = Get(c, "name") ?? throw new RowException("Enter the name.");
        var email = Get(c, "email", 254);
        if (email is not null && !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw new RowException($"E-mail \"{email}\" is not valid.");
        var emp = Get(c, "employeeId", 50);
        if (!seen.Add("n:" + name.ToLowerInvariant())) throw new RowException($"{name} appears more than once in the file.");
        var status = Pick(Get(c, "status"), [("active", "Active"), ("left", "Left")], "Status");
        var dep = await DepartmentAsync(x, Get(c, "department", 100));
        var loc = await LocationAsync(x, Get(c, "location", 100));
        Guid? id;
        await using (var f = new NpgsqlCommand("""
            SELECT id FROM zentriq.lifecycle_people
             WHERE (@emp::text IS NOT NULL AND lower(employee_id) = lower(@emp)) OR (@e::text IS NOT NULL AND lower(email) = lower(@e)) OR lower(btrim(name)) = lower(btrim(@n))
             ORDER BY (@emp::text IS NOT NULL AND lower(employee_id) = lower(@emp)) DESC, (@e::text IS NOT NULL AND lower(email) = lower(@e)) DESC LIMIT 1
            """, x.Conn, x.Tx))
        {
            f.Parameters.Add(T("emp", emp));
            f.Parameters.Add(T("e", email));
            f.Parameters.AddWithValue("n", name);
            id = await f.ExecuteScalarAsync(x.Ct) as Guid?;
        }
        if (id is not null && !x.O.UpdateExisting) { x.Result.Skipped++; return; }
        await using var cmd = new NpgsqlCommand(id is null ? """
            INSERT INTO zentriq.lifecycle_people (name, email, employee_id, title, phone, department_id, location_id, manager, status, notes, source, created_by)
            VALUES (@n, @e, @emp, @title, @phone, @dep, @loc, @mgr, COALESCE(@st, 'active'), @notes, 'import', @by)
            """ : """
            UPDATE zentriq.lifecycle_people SET name = @n, email = COALESCE(@e, email), employee_id = COALESCE(@emp, employee_id), title = COALESCE(@title, title),
                   phone = COALESCE(@phone, phone), department_id = COALESCE(@dep, department_id), location_id = COALESCE(@loc, location_id),
                   manager = COALESCE(@mgr, manager), status = COALESCE(@st, status), notes = COALESCE(@notes, notes), updated_at = now()
             WHERE id = @id
            """, x.Conn, x.Tx);
        cmd.Parameters.Add(U("id", id));
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.Add(T("e", email));
        cmd.Parameters.Add(T("emp", emp));
        cmd.Parameters.Add(T("title", Get(c, "title")));
        cmd.Parameters.Add(T("phone", Get(c, "phone", 50)));
        cmd.Parameters.Add(U("dep", dep));
        cmd.Parameters.Add(U("loc", loc));
        cmd.Parameters.Add(T("mgr", Get(c, "manager")));
        cmd.Parameters.Add(T("st", status));
        cmd.Parameters.Add(T("notes", Get(c, "notes", 4000)));
        cmd.Parameters.Add(T("by", x.Actor));
        await cmd.ExecuteNonQueryAsync(x.Ct);
        if (id is null) { x.Result.Created++; x.Result.NewPeople.Add(name); } else x.Result.Updated++;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Contracts

    private static async Task ContractRowAsync(RowContext x, Dictionary<string, string> c, HashSet<string> seen)
    {
        var name = Get(c, "name") ?? throw new RowException("Enter the contract name.");
        if (!seen.Add("c:" + name.ToLowerInvariant())) throw new RowException($"Contract {name} appears more than once in the file.");
        var kind = Pick(Get(c, "kind") ?? throw new RowException("Enter the type."), ContractKinds.Select(k => (k, k == "amc" ? "AMC" : char.ToUpperInvariant(k[0]) + k[1..])), "Type");
        var start = Date(c, "startDate", "Start date");
        var end = Date(c, "endDate", "End date");
        if (start is not null && end is not null && end < start) throw new RowException("The contract ends before it starts.");
        var cost = Num(c, "cost", "Cost");
        var cur = Get(c, "currency", 3)?.ToUpperInvariant();
        var notify = Num(c, "notifyDays", "Remind days before end");
        if (notify is > 365) throw new RowException("Remind at most 365 days before the end.");
        Guid? id;
        await using (var f = new NpgsqlCommand("SELECT id FROM zentriq.asset_contracts WHERE lower(name) = lower(@n) LIMIT 1", x.Conn, x.Tx))
        {
            f.Parameters.AddWithValue("n", name);
            id = await f.ExecuteScalarAsync(x.Ct) as Guid?;
        }
        if (id is not null && !x.O.UpdateExisting) { x.Result.Skipped++; return; }
        var isNew = id is null;
        await using (var cmd = new NpgsqlCommand(id is null ? """
            INSERT INTO zentriq.asset_contracts (name, kind, vendor, reference, start_date, end_date, cost, currency, notify_days, notes, created_by)
            VALUES (@n, @k, @v, @r, @s, @e, @c, COALESCE(@cur, @defcur), COALESCE(@nd, 90), @notes, @by) RETURNING id
            """ : """
            UPDATE zentriq.asset_contracts SET kind = @k, vendor = COALESCE(@v, vendor), reference = COALESCE(@r, reference), start_date = COALESCE(@s, start_date),
                   end_date = COALESCE(@e, end_date), cost = COALESCE(@c, cost), currency = COALESCE(@cur, currency), notify_days = COALESCE(@nd, notify_days),
                   notes = COALESCE(@notes, notes), updated_at = now() WHERE id = @id RETURNING id
            """, x.Conn, x.Tx))
        {
            cmd.Parameters.Add(U("id", id));
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("k", kind!);
            cmd.Parameters.Add(T("v", Get(c, "vendor")));
            cmd.Parameters.Add(T("r", Get(c, "reference", 100)));
            cmd.Parameters.Add(D("s", start));
            cmd.Parameters.Add(D("e", end));
            cmd.Parameters.Add(N("c", cost));
            cmd.Parameters.Add(T("cur", cur));
            cmd.Parameters.AddWithValue("defcur", x.Lk.Currency);
            cmd.Parameters.Add(new NpgsqlParameter("nd", NpgsqlDbType.Integer) { Value = notify is null ? DBNull.Value : (int)notify.Value });
            cmd.Parameters.Add(T("notes", Get(c, "notes", 4000)));
            cmd.Parameters.Add(T("by", x.Actor));
            id = (Guid)(await cmd.ExecuteScalarAsync(x.Ct))!;
        }
        if (Get(c, "assets", 20000) is { } list)
        {
            var wanted = list.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            await using var link = new NpgsqlCommand("""
                WITH t AS (SELECT unnest(@tags) AS tag),
                     f AS (SELECT t.tag, l.asset_id FROM t LEFT JOIN zentriq.asset_lifecycle l ON lower(l.asset_tag) = lower(t.tag)),
                     ins AS (INSERT INTO zentriq.asset_contract_assets (contract_id, asset_id) SELECT @id, asset_id FROM f WHERE asset_id IS NOT NULL ON CONFLICT DO NOTHING)
                SELECT string_agg(tag, ', ') FROM f WHERE asset_id IS NULL
                """, x.Conn, x.Tx);
            link.Parameters.AddWithValue("id", id);
            link.Parameters.Add(new NpgsqlParameter("tags", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = wanted });
            if (await link.ExecuteScalarAsync(x.Ct) is string missing) throw new RowException($"Asset tag(s) not found: {missing}.");
        }
        if (isNew) x.Result.Created++; else x.Result.Updated++;
    }
}
