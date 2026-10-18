namespace Zentriq.Infrastructure.NonIt;

public enum NonItFieldType { Text, Number, Date, List, Integer }

/// <summary>A standard field of a non-IT asset: database column, label (also the Excel column heading) and type.</summary>
public sealed record NonItField(string Key, string Column, string Label, NonItFieldType Type, bool Required = false, string? Hint = null, int MaxLength = 200);

/// <summary>The non-IT asset fields, in the order of the form, the Excel template and the export.</summary>
public static class NonItFields
{
    public static readonly string[] Statuses = ["in_use", "in_store", "under_repair", "under_maintenance", "reserved", "lost", "disposed"];
    public static readonly string[] Conditions = ["new", "good", "fair", "poor", "damaged"];

    public static string StatusLabel(string s) => s switch
    {
        "in_use" => "In use", "in_store" => "In store", "under_repair" => "Under repair", "under_maintenance" => "Under maintenance",
        "reserved" => "Reserved", "lost" => "Lost / missing", "disposed" => "Disposed", _ => s,
    };
    public static string ConditionLabel(string s) => s switch { "new" => "New", "good" => "Good", "fair" => "Fair", "poor" => "Poor", "damaged" => "Damaged", _ => s };

    public static readonly IReadOnlyList<NonItField> All =
    [
        new("assetTag", "asset_tag", "Asset tag", NonItFieldType.Text, Hint: "Leave empty to number automatically (NIT-000001 …). An existing tag updates that asset.", MaxLength: 60),
        new("name", "name", "Name", NonItFieldType.Text, Required: true, Hint: "What the item is, e.g. Ergonomic chair, Split AC 1.5 ton"),
        new("category", "category_id", "Category", NonItFieldType.List, Required: true, Hint: "One of the categories on the Lists sheet (custom categories too)"),
        new("manufacturer", "manufacturer", "Manufacturer", NonItFieldType.Text),
        new("model", "model", "Model", NonItFieldType.Text),
        new("serialNumber", "serial_number", "Serial number", NonItFieldType.Text, MaxLength: 100),
        new("barcode", "barcode", "Barcode", NonItFieldType.Text, Hint: "Code printed on the item's label; empty = the asset tag", MaxLength: 100),
        new("status", "status", "Status", NonItFieldType.List, Hint: "In use, In store, Under repair, Under maintenance, Reserved, Lost / missing, Disposed (default In use)"),
        new("condition", "condition", "Condition", NonItFieldType.List, Hint: "New, Good, Fair, Poor, Damaged"),
        new("quantity", "quantity", "Quantity", NonItFieldType.Integer, Hint: "Default 1"),
        new("location", "location", "Location", NonItFieldType.Text, Hint: "Site / building / floor / room"),
        new("department", "department_id", "Department", NonItFieldType.List, Hint: "A department defined in Zentriq"),
        new("assignedTo", "assigned_to", "Assigned to", NonItFieldType.Text, Hint: "Person or team using it"),
        new("purchaseDate", "purchase_date", "Purchase date", NonItFieldType.Date),
        new("vendor", "vendor", "Vendor", NonItFieldType.Text),
        new("purchaseCost", "purchase_cost", "Purchase cost", NonItFieldType.Number),
        new("invoiceNumber", "invoice_number", "Invoice number", NonItFieldType.Text, MaxLength: 100),
        new("poNumber", "po_number", "PO number", NonItFieldType.Text, MaxLength: 100),
        new("warrantyUntil", "warranty_until", "Warranty until", NonItFieldType.Date),
        new("amcVendor", "amc_vendor", "AMC vendor", NonItFieldType.Text),
        new("amcUntil", "amc_until", "AMC until", NonItFieldType.Date),
        new("insuranceUntil", "insurance_until", "Insurance until", NonItFieldType.Date),
        new("lastMaintenance", "last_maintenance", "Last maintenance", NonItFieldType.Date),
        new("nextMaintenance", "next_maintenance", "Next maintenance", NonItFieldType.Date),
        new("usefulLifeYears", "useful_life_years", "Useful life (years)", NonItFieldType.Number, Hint: "For straight-line depreciation"),
        new("salvageValue", "salvage_value", "Salvage value", NonItFieldType.Number),
        new("notes", "notes", "Notes", NonItFieldType.Text, MaxLength: 2000),
    ];

    /// <summary>Column heading → field key (headings match case-insensitively; a trailing * is ignored).</summary>
    public static string NormalizeHeading(string h) => new string(h.Trim().TrimEnd('*').Trim().ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());

    /// <summary>Current value with straight-line depreciation from the purchase date (null without cost, date and life).</summary>
    public const string CurrentValueSql = """
        CASE WHEN n.purchase_cost IS NULL THEN NULL
             WHEN n.purchase_date IS NULL OR COALESCE(n.useful_life_years, 0) <= 0 THEN n.purchase_cost
             ELSE round(GREATEST(COALESCE(n.salvage_value, 0), n.purchase_cost - (n.purchase_cost - COALESCE(n.salvage_value, 0))
                  * LEAST(1, (current_date - n.purchase_date) / (365.25 * n.useful_life_years))), 2) END
        """;
}
