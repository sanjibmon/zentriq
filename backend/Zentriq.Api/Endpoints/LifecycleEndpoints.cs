using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Lifecycle;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Asset Lifecycle for IT assets: stage and status (planned → on order → in stock → in use → in maintenance → retired →
/// disposed, plus missing), asset tags and labels, custody (assign / loan / return, handover), purchase, depreciation and
/// lease, warranty and contracts, refresh planning and disposal with data sanitisation (NIST SP 800-88).
/// Data is read from the view zentriq.v_asset_lifecycle; every change goes through <see cref="LifecycleStore"/>.
/// </summary>
public static class LifecycleEndpoints
{
    public sealed record StageBody(string Stage, string? Status, string? Reason, string? AssignedTo, string? AssignedEmail, DateOnly? ExpectedReturn,
        Guid? DepartmentId, Guid? LocationId, DateOnly? DisposedOn, string? DisposalVendor, decimal? DisposalValue,
        string? SanitizationMethod, string? SanitizedBy, DateOnly? SanitizedOn, string? CertificateRef);
    public sealed record BulkStageBody(Guid[] AssetIds, string Stage, string? Status, string? Reason);
    public sealed record ReturnBody(string? Stage, string? Status, string? Reason, Guid? LocationId);
    public sealed record DetailsBody(string? AssetTag, string? Acquisition, DateOnly? PurchaseDate, DateOnly? InServiceDate, decimal? PurchaseCost, string? Currency,
        string? Vendor, string? InvoiceNumber, string? PoNumber, string? DepreciationMethod, int? UsefulLifeMonths, decimal? SalvageValue,
        string? Lessor, DateOnly? LeaseStart, DateOnly? LeaseEnd, decimal? LeaseMonthlyCost,
        string? WarrantyProvider, string? WarrantyType, DateOnly? WarrantyStart, DateOnly? WarrantyEnd, string? Notes, int? RowVersion,
        Dictionary<string, string?>? Custom = null);
    public sealed record StockItem(string? Serial, string? Manufacturer, string? Model, string? DeviceType, string? Hostname, string? AssetTag = null);
    public sealed record StockBody(StockItem[] Items, string? Stage, string? Status, Guid? DepartmentId, Guid? LocationId,
        DateOnly? PurchaseDate, decimal? PurchaseCost, string? Currency, string? Vendor, string? InvoiceNumber, string? PoNumber,
        string? WarrantyProvider, DateOnly? WarrantyEnd, string? Notes);
    public sealed record ContractBody(string Name, string Kind, string? Vendor, string? Reference, DateOnly? StartDate, DateOnly? EndDate,
        decimal? Cost, string? Currency, int? NotifyDays, string? Notes);
    public sealed record ContractAssetsBody(Guid[]? Add, Guid[]? Remove);
    public sealed record PolicyBody(string TagPrefix, int UsefulLifeMonths, string DepreciationMethod, decimal SalvagePercent, decimal RefreshYears);
    public sealed record SettingsBody(string? Currency, int? NotSeenDays, bool? AutoTag, string? TagMode = null, string? TagFormat = null, string? TagOrg = null,
        int? TagStart = null, bool? TagDiscovered = null);
    public sealed record PersonBody(string Name, string? Email, string? EmployeeId, string? Title, string? Phone, Guid? DepartmentId, Guid? LocationId,
        string? Manager, string? Status, string? Notes);
    public sealed record FieldBody(string Label, string Type, string[]? Options, bool? Required, int? Sort);
    public sealed record IdsBody(Guid[] AssetIds);

    /// <summary>Columns of the view as camelCase JSON.</summary>
    private const string Cols = """
        v.asset_id AS "id", v.asset_tag AS "assetTag", COALESCE(v.hostname, v.serial, v.model) AS "name", v.hostname AS "hostname", v.serial AS "serial",
        v.manufacturer AS "manufacturer", v.model AS "model", v.class AS "class", v.class_label AS "classLabel", v.device_type AS "deviceType",
        v.category AS "category", v.os_name AS "osName", v.asset_status AS "assetStatus",
        v.stage AS "stage", v.status AS "status", v.stage_changed_at AS "stageChangedAt", v.stage_reason AS "stageReason", v.auto_deploy AS "awaitingFirstSighting",
        v.assigned_to AS "assignedTo", v.assigned_email AS "assignedEmail", v.assigned_at AS "assignedAt", v.expected_return AS "expectedReturn",
        v.department_id AS "departmentId", dp.name AS "department", v.location_id AS "locationId", lo.name AS "location",
        v.acquisition AS "acquisition", v.purchase_date AS "purchaseDate", v.in_service_date AS "inServiceDate", v.purchase_cost AS "purchaseCost",
        v.currency AS "currency", v.vendor AS "vendor", v.invoice_number AS "invoiceNumber", v.po_number AS "poNumber",
        v.depreciation_method AS "depreciationMethod", v.useful_life_months AS "usefulLifeMonths", v.salvage_value AS "salvageValue",
        v.depreciation_custom AS "depreciationCustom", v.book_value AS "bookValue",
        v.lessor AS "lessor", v.lease_start AS "leaseStart", v.lease_end AS "leaseEnd", v.lease_monthly_cost AS "leaseMonthlyCost",
        v.warranty_provider AS "warrantyProvider", v.warranty_type AS "warrantyType", v.warranty_start AS "warrantyStart", v.warranty_end AS "warrantyEnd",
        v.warranty_days AS "warrantyDays",
        v.disposed_on AS "disposedOn", v.disposal_method AS "disposalMethod", v.disposal_vendor AS "disposalVendor", v.disposal_value AS "disposalValue",
        v.sanitization_method AS "sanitizationMethod", v.sanitized_by AS "sanitizedBy", v.sanitized_on AS "sanitizedOn", v.certificate_ref AS "certificateRef",
        v.notes AS "notes", v.updated_at AS "updatedAt", v.updated_by AS "updatedBy", v.row_version AS "rowVersion",
        v.age_months AS "ageMonths", v.refresh_years AS "refreshYears", v.refresh_due AS "refreshDue", v.last_seen_at AS "lastSeenAt",
        v.first_seen_at AS "firstSeenAt", v.source AS "source"
        """;
    private const string From = """
        zentriq.v_asset_lifecycle v
        LEFT JOIN zentriq.departments dp ON dp.id = v.department_id
        LEFT JOIN zentriq.locations lo ON lo.id = v.location_id
        """;
    /// <summary>Operating systems out of vendor support (refresh candidates).</summary>
    private const string OsEndOfSupport = @"(v.os_name ~* '(windows (xp|vista|7|8(\.1)?|10)\b|server 20(03|08|12))')";

    private static NpgsqlParameter T(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(v) ? DBNull.Value : v.Trim() };
    private static NpgsqlParameter U(string n, Guid? v) => new(n, NpgsqlDbType.Uuid) { Value = (object?)v ?? DBNull.Value };
    private static NpgsqlParameter D(string n, DateOnly? v) => new(n, NpgsqlDbType.Date) { Value = (object?)v ?? DBNull.Value };
    private static NpgsqlParameter N(string n, decimal? v) => new(n, NpgsqlDbType.Numeric) { Value = (object?)v ?? DBNull.Value };
    private static string? Clean(string? s, int max = 200) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();

    private static async Task<JsonElement> JsonAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var json = await cmd.ExecuteScalarAsync(ct) as string ?? "null";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public static void MapLifecycleEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/lifecycle").WithTags("Asset lifecycle").RequireModule(Modules.Lifecycle).RequireAuthorization(Permissions.LifecycleView);

        // ---- reference data -------------------------------------------------------------------------------------
        g.MapGet("/meta", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT json_build_object(
                  'policies', (SELECT COALESCE(json_agg(json_build_object('class', class, 'label', label, 'tagPrefix', tag_prefix, 'usefulLifeMonths', useful_life_months,
                                  'depreciationMethod', depreciation_method, 'salvagePercent', salvage_percent, 'refreshYears', refresh_years) ORDER BY array_position(@classes, class)), '[]')
                                 FROM zentriq.lifecycle_policies),
                  'currency', COALESCE((SELECT value #>> '{}' FROM zentriq.system_settings WHERE key = 'lifecycle.currency'), 'INR'),
                  'notSeenDays', COALESCE((SELECT (value #>> '{}')::int FROM zentriq.system_settings WHERE key = 'lifecycle.not_seen_days'), 60),
                  'autoTag', COALESCE((SELECT (value #>> '{}')::boolean FROM zentriq.system_settings WHERE key = 'lifecycle.auto_tag'), true),
                  'fields', (SELECT COALESCE(json_agg(json_build_object('id', f.id, 'key', f.key, 'label', f.label, 'type', f.type, 'options', f.options,
                                'required', f.required, 'sort', f.sort) ORDER BY f.sort, lower(f.label)), '[]') FROM zentriq.lifecycle_fields f))::text
                """);
            cmd.Parameters.Add(new NpgsqlParameter("classes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = LifecycleStore.Classes });
            var m = await JsonAsync(cmd, ct);
            TagSettings ts;
            await using (var tconn = await ds.OpenConnectionAsync(ct)) ts = await TagSettings.LoadAsync(tconn, null, ct);
            var today = DateOnly.FromDateTime(DateTime.Today);
            return Results.Ok(new
            {
                stages = LifecycleStore.Stages.Select(s => new { id = s.Stage, label = s.Label, statuses = s.Statuses.Select(x => new { id = x, label = LifecycleStore.StatusLabel(x) }), next = LifecycleStore.Transitions[s.Stage] }),
                policies = m.GetProperty("policies"), currency = m.GetProperty("currency").GetString(), notSeenDays = m.GetProperty("notSeenDays").GetInt32(),
                autoTag = ts.Auto,
                tags = new { mode = ts.Mode, format = ts.Format, org = ts.Org, start = ts.Start, discovered = ts.TagDiscovered,
                    example = TagFormat.Render(ts.Format, "LAP", ts.Org, "PUNE", today, ts.Start) },
                fields = m.GetProperty("fields"),
                sanitizationMethods = new[] { new { id = "clear", label = "Clear (overwrite, NIST SP 800-88)" }, new { id = "purge", label = "Purge (secure / crypto erase, degauss)" },
                    new { id = "destroy", label = "Destroy (shred, disintegrate, incinerate)" }, new { id = "not_required", label = "Not required (no data storage)" } },
                depreciationMethods = new[] { new { id = "straight_line", label = "Straight-line" }, new { id = "declining_balance", label = "Declining balance (double)" }, new { id = "none", label = "Not depreciated" } },
                contractKinds = new[] { "warranty", "amc", "support", "lease", "insurance", "other" },
                acquisitions = new[] { "purchase", "lease", "rental", "donation", "byod" },
            });
        });

        g.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($$"""
                SELECT json_build_object(
                  'stages', (SELECT COALESCE(json_object_agg(stage, n), '{}') FROM (SELECT stage, count(*) n FROM zentriq.v_asset_lifecycle GROUP BY stage) x),
                  'total', (SELECT count(*) FROM zentriq.v_asset_lifecycle WHERE stage <> 'disposed'),
                  'purchaseValue', (SELECT COALESCE(sum(purchase_cost), 0) FROM zentriq.v_asset_lifecycle WHERE stage <> 'disposed'),
                  'bookValue', (SELECT COALESCE(sum(book_value), 0) FROM zentriq.v_asset_lifecycle WHERE stage <> 'disposed'),
                  'withCost', (SELECT count(*) FROM zentriq.v_asset_lifecycle WHERE stage <> 'disposed' AND purchase_cost IS NOT NULL),
                  'warranty30', (SELECT count(*) FROM zentriq.v_asset_lifecycle WHERE stage NOT IN ('retired','disposed') AND warranty_days BETWEEN 0 AND 30),
                  'warranty90', (SELECT count(*) FROM zentriq.v_asset_lifecycle WHERE stage NOT IN ('retired','disposed') AND warranty_days BETWEEN 0 AND 90),
                  'warrantyExpired', (SELECT count(*) FROM zentriq.v_asset_lifecycle WHERE stage IN ('in_use','in_stock') AND warranty_days < 0),
                  'noWarranty', (SELECT count(*) FROM zentriq.v_asset_lifecycle WHERE stage IN ('in_use','in_stock') AND warranty_end IS NULL),
                  'contracts90', (SELECT count(*) FROM zentriq.asset_contracts WHERE end_date BETWEEN current_date AND current_date + 90),
                  'loansOverdue', (SELECT count(*) FROM zentriq.v_asset_lifecycle WHERE status = 'loaned' AND expected_return < current_date),
                  'refreshDue', (SELECT count(*) FROM zentriq.v_asset_lifecycle v WHERE stage IN ('in_use','in_stock') AND (refresh_due OR {{OsEndOfSupport}})),
                  'unassigned', (SELECT count(*) FROM zentriq.v_asset_lifecycle WHERE stage = 'in_use' AND status = 'in_use' AND class IN ('laptop','desktop','mobile')),
                  'untagged', (SELECT count(*) FROM zentriq.v_asset_lifecycle WHERE stage <> 'disposed' AND asset_tag IS NULL),
                  'byClass', (SELECT COALESCE(json_agg(x ORDER BY x.n DESC), '[]') FROM (SELECT class_label AS label, class, count(*) n, COALESCE(sum(book_value), 0) AS "bookValue"
                                FROM zentriq.v_asset_lifecycle WHERE stage <> 'disposed' GROUP BY class_label, class) x),
                  'recent', (SELECT COALESCE(json_agg(x), '[]') FROM (
                                SELECT e.id, e.at, e.kind, e.from_stage AS "fromStage", e.to_stage AS "toStage", e.to_status AS "toStatus", e.person, e.reason, e.actor,
                                       e.asset_id AS "assetId", COALESCE(l.asset_tag, '') AS "assetTag", COALESCE(a.hostname, a.serial_number, a.manual_serial) AS "name"
                                  FROM zentriq.asset_lifecycle_events e JOIN zentriq.assets a ON a.id = e.asset_id JOIN zentriq.asset_lifecycle l ON l.asset_id = a.id
                                 ORDER BY e.id DESC LIMIT 12) x),
                  'currency', COALESCE((SELECT value #>> '{}' FROM zentriq.system_settings WHERE key = 'lifecycle.currency'), 'INR'))::text
                """);
            return Results.Ok(await JsonAsync(cmd, ct));
        });

        // ---- assets ---------------------------------------------------------------------------------------------
        g.MapGet("/assets", async (NpgsqlDataSource ds, string? q, string? stage, string? status, string? @class, Guid? departmentId, Guid? locationId,
            string? warranty, string? person, bool? refresh, bool? untagged, string? sort, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, size) = Paging.Normalize(page, pageSize);
            var order = sort switch
            {
                "tag" => "lower(v.asset_tag) NULLS LAST", "stage" => "array_position(@stages, v.stage), v.status", "person" => "lower(v.assigned_to) NULLS LAST",
                "warranty" => "v.warranty_end NULLS LAST", "value" => "v.book_value DESC NULLS LAST", "age" => "v.age_months DESC NULLS LAST",
                "changed" => "v.stage_changed_at DESC", _ => "lower(COALESCE(v.hostname, v.serial, v.model)) NULLS LAST",
            };
            await using var cmd = ds.CreateCommand($$"""
                WITH f AS (
                    SELECT {{Cols}}, count(*) OVER () AS "total", row_number() OVER (ORDER BY {{order}}) AS "rn" FROM {{From}}
                     WHERE (@stage::text IS NULL AND v.stage <> 'disposed' OR v.stage = @stage OR (@stage = 'all'))
                       AND (@status::text IS NULL OR v.status = @status)
                       AND (@class::text IS NULL OR v.class = @class)
                       AND (@dep::uuid IS NULL OR v.department_id = @dep) AND (@loc::uuid IS NULL OR v.location_id = @loc)
                       AND (@person::text IS NULL OR lower(v.assigned_to) = lower(@person))
                       AND (@w::text IS NULL OR (@w = 'expiring' AND v.warranty_days BETWEEN 0 AND 90) OR (@w = 'expired' AND v.warranty_days < 0)
                            OR (@w = 'none' AND v.warranty_end IS NULL) OR (@w = 'active' AND v.warranty_days >= 0))
                       AND (NOT @refresh OR (v.stage IN ('in_use','in_stock') AND (v.refresh_due OR {{OsEndOfSupport}})))
                       AND (NOT @untagged OR v.asset_tag IS NULL)
                       AND (@q::text IS NULL OR v.asset_tag ILIKE @q OR v.hostname ILIKE @q OR v.serial ILIKE @q OR v.model ILIKE @q OR v.manufacturer ILIKE @q
                            OR v.assigned_to ILIKE @q OR v.vendor ILIKE @q OR v.po_number ILIKE @q OR v.invoice_number ILIKE @q)
                     ORDER BY {{order}} LIMIT @l OFFSET @o)
                SELECT json_build_object('items', COALESCE(json_agg(f ORDER BY f."rn"), '[]'), 'total', COALESCE(max(f."total"), 0), 'page', @page, 'pageSize', @l)::text FROM f
                """);
            cmd.Parameters.Add(T("stage", stage));
            cmd.Parameters.Add(T("status", status));
            cmd.Parameters.Add(T("class", @class));
            cmd.Parameters.Add(U("dep", departmentId));
            cmd.Parameters.Add(U("loc", locationId));
            cmd.Parameters.Add(T("person", person));
            cmd.Parameters.Add(T("w", warranty));
            cmd.Parameters.AddWithValue("refresh", refresh == true);
            cmd.Parameters.AddWithValue("untagged", untagged == true);
            cmd.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
            cmd.Parameters.Add(new NpgsqlParameter("stages", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = LifecycleStore.Stages.Select(s => s.Stage).ToArray() });
            cmd.Parameters.AddWithValue("l", size);
            cmd.Parameters.AddWithValue("o", (p - 1) * size);
            cmd.Parameters.AddWithValue("page", p);
            var result = await JsonAsync(cmd, ct);
            if (result.GetProperty("total").GetInt64() == 0 && p == 1) return Results.Ok(new { items = Array.Empty<object>(), total = 0, page = p, pageSize = size });
            return Results.Ok(result);
        });

        g.MapGet("/assets/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($$"""
                SELECT json_build_object(
                  'asset', (SELECT row_to_json(x) FROM (SELECT {{Cols}} FROM {{From}} WHERE v.asset_id = @id) x),
                  'events', (SELECT COALESCE(json_agg(x ORDER BY x.id DESC), '[]') FROM (
                        SELECT e.id, e.at, e.actor, e.kind, e.from_stage AS "fromStage", e.to_stage AS "toStage", e.from_status AS "fromStatus", e.to_status AS "toStatus",
                               e.person, e.reason, e.detail FROM zentriq.asset_lifecycle_events e WHERE e.asset_id = @id ORDER BY e.id DESC LIMIT 200) x),
                  'contracts', (SELECT COALESCE(json_agg(x ORDER BY x."endDate" NULLS LAST), '[]') FROM (
                        SELECT c.id, c.name, c.kind, c.vendor, c.reference, c.start_date AS "startDate", c.end_date AS "endDate"
                          FROM zentriq.asset_contracts c JOIN zentriq.asset_contract_assets ca ON ca.contract_id = c.id WHERE ca.asset_id = @id) x),
                  'osEndOfSupport', (SELECT {{OsEndOfSupport}} FROM zentriq.v_asset_lifecycle v WHERE v.asset_id = @id),
                  'custom', (SELECT l.custom FROM zentriq.asset_lifecycle l WHERE l.asset_id = @id))::text
                """);
            cmd.Parameters.AddWithValue("id", id);
            var r = await JsonAsync(cmd, ct);
            if (r.GetProperty("asset").ValueKind == JsonValueKind.Null) return Results.NotFound();
            var a = r.GetProperty("asset");
            var stage = a.GetProperty("stage").GetString()!;
            object? schedule = null;
            if (a.GetProperty("purchaseCost").ValueKind == JsonValueKind.Number && a.GetProperty("usefulLifeMonths").ValueKind == JsonValueKind.Number)
            {
                var start = a.GetProperty("purchaseDate").ValueKind == JsonValueKind.String ? DateOnly.Parse(a.GetProperty("purchaseDate").GetString()!)
                          : a.GetProperty("inServiceDate").ValueKind == JsonValueKind.String ? DateOnly.Parse(a.GetProperty("inServiceDate").GetString()!) : (DateOnly?)null;
                if (start is { } s0)
                    schedule = LifecycleStore.Schedule(a.GetProperty("purchaseCost").GetDecimal(),
                        a.GetProperty("salvageValue").ValueKind == JsonValueKind.Number ? a.GetProperty("salvageValue").GetDecimal() : null,
                        a.GetProperty("depreciationMethod").GetString() ?? "straight_line", a.GetProperty("usefulLifeMonths").GetInt32(), s0)
                        .Select(x => new { year = x.Year, opening = x.Opening, depreciation = x.Depreciation, closing = x.Closing });
            }
            return Results.Ok(new
            {
                asset = a, events = r.GetProperty("events"), contracts = r.GetProperty("contracts"), osEndOfSupport = r.GetProperty("osEndOfSupport"),
                next = LifecycleStore.Transitions[stage], schedule, custom = r.GetProperty("custom"),
            });
        });

        g.MapPut("/assets/{id:guid}", async (Guid id, DetailsBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (b.Acquisition is { } acq && !new[] { "purchase", "lease", "rental", "donation", "byod" }.Contains(acq)) return Validation.Problem("acquisition", "Unknown acquisition type.");
            if (b.DepreciationMethod is { } dm && !LifecycleStore.DepreciationMethods.Contains(dm)) return Validation.Problem("depreciationMethod", "Unknown depreciation method.");
            if (b.UsefulLifeMonths is < 0 or > 600) return Validation.Problem("usefulLifeMonths", "Useful life must be between 0 and 600 months.");
            if (b.PurchaseCost is < 0 || b.SalvageValue is < 0 || b.LeaseMonthlyCost is < 0) return Validation.Problem("purchaseCost", "Amounts cannot be negative.");
            if (b.SalvageValue is { } sv && b.PurchaseCost is { } pc && sv > pc) return Validation.Problem("salvageValue", "The salvage value cannot be more than the purchase cost.");
            if (b.WarrantyStart is { } ws && b.WarrantyEnd is { } we && we < ws) return Validation.Problem("warrantyEnd", "The warranty ends before it starts.");
            if (b.LeaseStart is { } ls && b.LeaseEnd is { } le && le < ls) return Validation.Problem("leaseEnd", "The lease ends before it starts.");
            var tag = Clean(b.AssetTag, 40);
            if (tag is not null && !TagFormat.TagPattern.IsMatch(tag))
                return Validation.Problem("assetTag", "Asset tags use letters, digits and . _ / - (up to 40 characters).");
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var custom = new System.Text.Json.Nodes.JsonObject();
            if (b.Custom is not null)
            {
                foreach (var f in await LifecycleFields.LoadAsync(conn, tx, ct))
                {
                    try { custom[f.Key] = LifecycleFields.Parse(f, b.Custom.GetValueOrDefault(f.Key)); }
                    catch (LifecycleValidationException ex) { return Validation.Problem(ex.Field, ex.Message); }
                }
            }
            await using (var dup = new NpgsqlCommand("SELECT 1 FROM zentriq.asset_lifecycle WHERE lower(asset_tag) = lower(@t) AND asset_id <> @id", conn, tx))
            {
                dup.Parameters.Add(T("t", tag));
                dup.Parameters.AddWithValue("id", id);
                if (tag is not null && await dup.ExecuteScalarAsync(ct) is not null) return Validation.Problem("assetTag", $"Asset tag {tag} is already used by another asset.");
            }
            await using var cmd = new NpgsqlCommand("""
                UPDATE zentriq.asset_lifecycle SET asset_tag = @tag, acquisition = COALESCE(@acq, acquisition), purchase_date = @pd, in_service_date = COALESCE(@isd, in_service_date),
                       purchase_cost = @cost, currency = @cur, vendor = @vendor, invoice_number = @inv, po_number = @po,
                       depreciation_method = @dm, useful_life_months = @life, salvage_value = @salv,
                       lessor = @lessor, lease_start = @lst, lease_end = @lend, lease_monthly_cost = @lmc,
                       warranty_provider = @wp, warranty_type = @wt, warranty_start = @wst, warranty_end = @wend, notes = @notes,
                       custom = CASE WHEN @hasCustom THEN @custom ELSE custom END,
                       updated_at = now(), updated_by = @by, row_version = row_version + 1
                 WHERE asset_id = @id AND (@rv::int IS NULL OR row_version = @rv)
                RETURNING row_version
                """, conn, tx);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.Add(T("tag", tag));
            cmd.Parameters.Add(T("acq", b.Acquisition));
            cmd.Parameters.Add(D("pd", b.PurchaseDate));
            cmd.Parameters.Add(D("isd", b.InServiceDate));
            cmd.Parameters.Add(N("cost", b.PurchaseCost));
            cmd.Parameters.Add(T("cur", Clean(b.Currency, 3)?.ToUpperInvariant()));
            cmd.Parameters.Add(T("vendor", Clean(b.Vendor)));
            cmd.Parameters.Add(T("inv", Clean(b.InvoiceNumber, 100)));
            cmd.Parameters.Add(T("po", Clean(b.PoNumber, 100)));
            cmd.Parameters.Add(T("dm", b.DepreciationMethod));
            cmd.Parameters.Add(new NpgsqlParameter("life", NpgsqlDbType.Integer) { Value = (object?)b.UsefulLifeMonths ?? DBNull.Value });
            cmd.Parameters.Add(N("salv", b.SalvageValue));
            cmd.Parameters.Add(T("lessor", Clean(b.Lessor)));
            cmd.Parameters.Add(D("lst", b.LeaseStart));
            cmd.Parameters.Add(D("lend", b.LeaseEnd));
            cmd.Parameters.Add(N("lmc", b.LeaseMonthlyCost));
            cmd.Parameters.Add(T("wp", Clean(b.WarrantyProvider)));
            cmd.Parameters.Add(T("wt", Clean(b.WarrantyType, 100)));
            cmd.Parameters.Add(D("wst", b.WarrantyStart));
            cmd.Parameters.Add(D("wend", b.WarrantyEnd));
            cmd.Parameters.Add(T("notes", Clean(b.Notes, 4000)));
            cmd.Parameters.Add(T("by", rc.UserName));
            cmd.Parameters.Add(new NpgsqlParameter("rv", NpgsqlDbType.Integer) { Value = (object?)b.RowVersion ?? DBNull.Value });
            cmd.Parameters.AddWithValue("hasCustom", b.Custom is not null);
            cmd.Parameters.Add(new NpgsqlParameter("custom", NpgsqlDbType.Jsonb) { Value = custom.ToJsonString() });
            if (await cmd.ExecuteScalarAsync(ct) is not int)
                return Results.Problem(statusCode: 409, title: "The asset was changed by someone else (or does not exist). Reload and try again.");
            await LifecycleStore.EventAsync(conn, tx, id, rc.UserName, "updated", null, null, null, null, null, null,
                new { tag, b.PurchaseDate, b.PurchaseCost, b.WarrantyEnd, b.Vendor, b.DepreciationMethod, b.UsefulLifeMonths }, ct);
            await tx.CommitAsync(ct);
            await rc.AuditAsync("lifecycle.update", tag ?? id.ToString(), detail: new { id, tag, b.PurchaseCost, b.WarrantyEnd });
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(Permissions.LifecycleManage);

        async Task<IResult> Change(Guid id, StageChange change, NpgsqlDataSource ds, RequestContext rc, string audit, CancellationToken ct)
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                var ev = await LifecycleStore.ChangeAsync(conn, tx, id, change, rc.UserName, ct);
                await tx.CommitAsync(ct);
                await rc.AuditAsync(audit, id.ToString(), detail: new { id, change.Stage, change.Status, change.AssignedTo, change.Reason, eventId = ev });
                return Results.Ok(new { eventId = ev, message = $"{LifecycleStore.StageLabel(change.Stage)} · {LifecycleStore.StatusLabel(change.Status ?? LifecycleStore.DefaultStatus(change.Stage, change.AssignedTo))}" });
            }
            catch (LifecycleValidationException ex) { return Validation.Problem(ex.Field, ex.Message); }
        }

        g.MapPost("/assets/{id:guid}/stage", (Guid id, StageBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
            Change(id, new StageChange(b.Stage, b.Status, b.Reason, b.AssignedTo, b.AssignedEmail, b.ExpectedReturn, b.DepartmentId, b.LocationId,
                b.DisposedOn, null, b.DisposalVendor, b.DisposalValue, b.SanitizationMethod, b.SanitizedBy, b.SanitizedOn, b.CertificateRef), ds, rc,
                b.Stage == "disposed" ? "lifecycle.dispose" : "lifecycle.stage", ct)).RequireAuthorization(Permissions.LifecycleManage);

        g.MapPost("/assets/{id:guid}/return", (Guid id, ReturnBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
            Change(id, new StageChange(b.Stage ?? "in_stock", b.Status, b.Reason ?? "Returned", LocationId: b.LocationId), ds, rc, "lifecycle.return", ct))
            .RequireAuthorization(Permissions.LifecycleManage);

        g.MapPost("/stage", async (BulkStageBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (b.AssetIds is not { Length: > 0 and <= 500 }) return Validation.Problem("assetIds", "Select between 1 and 500 assets.");
            if (b.Stage == "disposed" || (b.Stage == "in_use" && b.Status is "assigned" or "loaned"))
                return Validation.Problem("stage", "Assign and dispose assets one at a time (they need a person, or disposal and data sanitisation details).");
            var changed = 0;
            var errors = new List<object>();
            await using var conn = await ds.OpenConnectionAsync(ct);
            foreach (var id in b.AssetIds.Distinct())
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                try
                {
                    await LifecycleStore.ChangeAsync(conn, tx, id, new StageChange(b.Stage, b.Status, b.Reason), rc.UserName, ct);
                    await tx.CommitAsync(ct);
                    changed++;
                }
                catch (LifecycleValidationException ex) { await tx.RollbackAsync(ct); errors.Add(new { assetId = id, message = ex.Message }); }
            }
            await rc.AuditAsync("lifecycle.stage", $"{changed} asset(s)", detail: new { b.Stage, b.Status, b.Reason, changed, failed = errors.Count });
            return Results.Ok(new { changed, errors });
        }).RequireAuthorization(Permissions.LifecycleManage);

        // ---- add to stock / on order (devices Zentriq has not seen yet) -------------------------------------------
        g.MapPost("/stock", async (StockBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var stage = b.Stage ?? "in_stock";
            if (stage is not ("planned" or "on_order" or "in_stock")) return Validation.Problem("stage", "New items are Planned, On order or In stock.");
            var status = b.Status ?? LifecycleStore.DefaultStatus(stage, null);
            if (!LifecycleStore.Stages.First(s => s.Stage == stage).Statuses.Contains(status)) return Validation.Problem("status", "Unknown status for this stage.");
            if (b.Items is not { Length: > 0 and <= 500 }) return Validation.Problem("items", "Add between 1 and 500 items.");
            var items = b.Items.Select(i => i with { Serial = Clean(i.Serial, 100), Manufacturer = Clean(i.Manufacturer), Model = Clean(i.Model), Hostname = Clean(i.Hostname, 63), AssetTag = Clean(i.AssetTag, 40) }).ToList();
            if (items.FirstOrDefault(i => i.AssetTag is not null && !TagFormat.TagPattern.IsMatch(i.AssetTag)) is { } badTag)
                return Validation.Problem("items", $"Asset tag {badTag.AssetTag}: use letters, digits and . _ / - (up to 40 characters).");
            var dupTags = items.Where(i => i.AssetTag is not null).GroupBy(i => i.AssetTag!.ToUpperInvariant()).Where(g2 => g2.Count() > 1).Select(g2 => g2.Key).ToList();
            if (dupTags.Count > 0) return Validation.Problem("items", "Asset tag(s) entered twice: " + string.Join(", ", dupTags.Take(5)));
            if (items.Any(i => i.Serial is null && i.Hostname is null)) return Validation.Problem("items", "Every item needs a serial number (or a host name).");
            if (items.Any(i => i.DeviceType is not null && !Zentriq.Inventory.DeviceTypes.IsKnown(i.DeviceType))) return Validation.Problem("items", "Unknown device type.");
            var dupSerials = items.Where(i => i.Serial is not null).GroupBy(i => i.Serial!.ToUpperInvariant()).Where(g2 => g2.Count() > 1).Select(g2 => g2.Key).ToList();
            if (dupSerials.Count > 0) return Validation.Problem("items", "Serial number(s) entered twice: " + string.Join(", ", dupSerials.Take(5)));
            if (b.PurchaseCost is < 0) return Validation.Problem("purchaseCost", "The cost cannot be negative.");

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var exist = new NpgsqlCommand("""
                SELECT string_agg(DISTINCT s, ', ') FROM unnest(@s) AS s
                 WHERE EXISTS (SELECT 1 FROM zentriq.assets a LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
                                WHERE a.status IN ('active','retired') AND (upper(a.serial_number) = upper(s) OR upper(h.serial_number) = upper(s) OR upper(a.manual_serial) = upper(s)))
                """, conn, tx))
            {
                exist.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = items.Where(i => i.Serial is not null).Select(i => i.Serial!).ToArray() });
                if (await exist.ExecuteScalarAsync(ct) is string known) return Validation.Problem("items", $"Already in the inventory: {known}. Open the device and change its lifecycle stage instead.");
            }
            var tagSettings = await TagSettings.LoadAsync(conn, tx, ct);
            var created = new List<object>();
            foreach (var i in items)
            {
                var id = Guid.NewGuid();
                var type = i.DeviceType ?? "laptop";
                var cat = Zentriq.Inventory.DeviceTypes.Get(type)?.Category ?? "endpoint";
                await using (var ins = new NpgsqlCommand("""
                    INSERT INTO zentriq.assets (id, hostname, serial_number, manufacturer, model, category, device_type, device_type_manual, source, department_id, location_id)
                    VALUES (@id, @h, @sn, @mf, @md, @cat, @t, true, 'lifecycle', @dep, @loc)
                    """, conn, tx))
                {
                    ins.Parameters.AddWithValue("id", id);
                    ins.Parameters.Add(T("h", i.Hostname));
                    ins.Parameters.Add(T("sn", i.Serial));
                    ins.Parameters.Add(T("mf", i.Manufacturer));
                    ins.Parameters.Add(T("md", i.Model));
                    ins.Parameters.AddWithValue("cat", cat);
                    ins.Parameters.AddWithValue("t", type);
                    ins.Parameters.Add(U("dep", b.DepartmentId));
                    ins.Parameters.Add(U("loc", b.LocationId));
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await using (var upd = new NpgsqlCommand("""
                    UPDATE zentriq.asset_lifecycle SET stage = @stage, status = @status, auto_deploy = true, in_service_date = NULL, stage_reason = 'Added to the register',
                           purchase_date = @pd, purchase_cost = @cost, currency = @cur, vendor = @vendor, invoice_number = @inv, po_number = @po,
                           warranty_provider = @wp, warranty_end = @wend, warranty_start = CASE WHEN @wend::date IS NOT NULL THEN @pd END, notes = @notes, updated_by = @by
                     WHERE asset_id = @id
                    """, conn, tx))
                {
                    upd.Parameters.AddWithValue("id", id);
                    upd.Parameters.AddWithValue("stage", stage);
                    upd.Parameters.AddWithValue("status", status);
                    upd.Parameters.Add(D("pd", b.PurchaseDate));
                    upd.Parameters.Add(N("cost", b.PurchaseCost));
                    upd.Parameters.Add(T("cur", Clean(b.Currency, 3)?.ToUpperInvariant()));
                    upd.Parameters.Add(T("vendor", Clean(b.Vendor)));
                    upd.Parameters.Add(T("inv", Clean(b.InvoiceNumber, 100)));
                    upd.Parameters.Add(T("po", Clean(b.PoNumber, 100)));
                    upd.Parameters.Add(T("wp", Clean(b.WarrantyProvider)));
                    upd.Parameters.Add(D("wend", b.WarrantyEnd));
                    upd.Parameters.Add(T("notes", Clean(b.Notes, 4000)));
                    upd.Parameters.Add(T("by", rc.UserName));
                    await upd.ExecuteNonQueryAsync(ct);
                }
                string? tag = null;
                try
                {
                    if (i.AssetTag is not null) { await LifecycleStore.SetTagAsync(conn, tx, id, i.AssetTag, ct); tag = i.AssetTag; }
                    else if (tagSettings.Auto) tag = await LifecycleStore.EnsureTagAsync(conn, tx, id, ct, tagSettings);
                }
                catch (LifecycleValidationException ex) { return Validation.Problem("items", ex.Message); }
                await LifecycleStore.EventAsync(conn, tx, id, rc.UserName, "created", null, stage, null, status, null, "Added to the register", new { i.Serial, i.Model, b.PoNumber }, ct);
                created.Add(new { id, tag, serial = i.Serial });
            }
            await tx.CommitAsync(ct);
            await rc.AuditAsync("lifecycle.stock", $"{created.Count} item(s)", detail: new { stage, status, count = created.Count, b.PoNumber, b.Vendor });
            return Results.Ok(new { created, message = $"{created.Count} item(s) added as {LifecycleStore.StageLabel(stage)} · {LifecycleStore.StatusLabel(status)}. When Zentriq sees one (agent or scan, same serial number) it moves to In use by itself." });
        }).RequireAuthorization(Permissions.LifecycleManage);

        // ---- tags and labels ------------------------------------------------------------------------------------
        g.MapPost("/tags", async (IdsBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (b.AssetIds is not { Length: > 0 and <= 1000 }) return Validation.Problem("assetIds", "Select between 1 and 1000 assets.");
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            var tags = new Dictionary<Guid, string>();
            foreach (var id in b.AssetIds.Distinct())
            {
                await using (var ok = new NpgsqlCommand("SELECT 1 FROM zentriq.asset_lifecycle WHERE asset_id = @a", conn, tx))
                {
                    ok.Parameters.AddWithValue("a", id);
                    if (await ok.ExecuteScalarAsync(ct) is null) continue;
                }
                tags[id] = await LifecycleStore.EnsureTagAsync(conn, tx, id, ct);
            }
            await tx.CommitAsync(ct);
            await rc.AuditAsync("lifecycle.tags", $"{tags.Count} asset(s)", detail: new { count = tags.Count });
            return Results.Ok(new { tags });
        }).RequireAuthorization(Permissions.LifecycleManage);

        g.MapGet("/labels", async (string ids, HttpContext http, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var list = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Guid.TryParse(x, out var gid) ? gid : Guid.Empty).Where(x => x != Guid.Empty).Distinct().Take(500).ToArray();
            await using var cmd = ds.CreateCommand("""
                SELECT v.asset_id, v.asset_tag, COALESCE(v.hostname, v.model), v.serial, v.model, v.class_label
                  FROM zentriq.v_asset_lifecycle v WHERE v.asset_id = ANY(@ids) ORDER BY lower(v.asset_tag) NULLS LAST
                """);
            cmd.Parameters.AddWithValue("ids", list);
            var origin = $"{http.Request.Scheme}://{http.Request.Host}";
            var labels = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetGuid(0);
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var url = $"{origin}/lifecycle/assets/{id}";
                labels.Add(new { id, tag = S(1), name = S(2), serial = S(3), model = S(4), @class = S(5), url, qrSvg = AccountEndpoints.QrSvg(url) });
            }
            return Results.Ok(labels);
        });

        // ---- people (custody) -----------------------------------------------------------------------------------
        g.MapGet("/people", async (NpgsqlDataSource ds, string? q, string? status, bool? holding, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                WITH it AS (
                    SELECT lower(btrim(v.assigned_to)) AS k, (array_agg(v.assigned_to ORDER BY v.assigned_at DESC NULLS LAST))[1] AS person,
                           (array_agg(v.assigned_email ORDER BY v.assigned_at DESC NULLS LAST) FILTER (WHERE v.assigned_email IS NOT NULL))[1] AS email,
                           (array_agg(dp.name ORDER BY v.assigned_at DESC NULLS LAST) FILTER (WHERE dp.name IS NOT NULL))[1] AS department,
                           count(*) AS assets, count(*) FILTER (WHERE v.status = 'loaned') AS loans,
                           count(*) FILTER (WHERE v.status = 'loaned' AND v.expected_return < current_date) AS overdue,
                           COALESCE(sum(v.book_value), 0) AS value
                      FROM zentriq.v_asset_lifecycle v LEFT JOIN zentriq.departments dp ON dp.id = v.department_id
                     WHERE v.assigned_to IS NOT NULL AND v.stage IN ('in_use','in_maintenance','missing') GROUP BY lower(btrim(v.assigned_to))),
                acc AS (
                    SELECT person_key AS k, sum(held) AS accessories FROM (
                        SELECT m.person_key, m.item_id, sum(CASE m.kind WHEN 'issue' THEN -m.quantity ELSE -(m.quantity + m.faulty_change) END) AS held
                          FROM zentriq.accessory_movements m JOIN zentriq.accessory_items i ON i.id = m.item_id
                         WHERE m.kind IN ('issue','return') AND m.person_key IS NOT NULL AND i.returnable GROUP BY m.person_key, m.item_id) x
                     WHERE held > 0 GROUP BY person_key),
                dir AS (SELECT p.*, lower(btrim(p.name)) AS k FROM zentriq.lifecycle_people p)
                SELECT COALESCE(json_agg(x ORDER BY lower(x.person)), '[]')::text FROM (
                    SELECT dir.id, COALESCE(dir.name, it.person) AS person, COALESCE(dir.email, it.email) AS email, dir.employee_id AS "employeeId",
                           dir.title, dir.phone, dir.manager, dir.notes, COALESCE(dir.status, 'active') AS status, dir.source,
                           COALESCE(dd.name, it.department) AS department, dir.department_id AS "departmentId", lo.name AS location, dir.location_id AS "locationId",
                           COALESCE(it.assets, 0) AS assets, COALESCE(it.loans, 0) AS loans, COALESCE(it.overdue, 0) AS overdue, COALESCE(it.value, 0) AS value,
                           COALESCE(acc.accessories, 0) AS accessories
                      FROM dir FULL JOIN it ON it.k = dir.k
                      LEFT JOIN acc ON acc.k = regexp_replace(COALESCE(dir.k, it.k), '\s+', ' ', 'g')
                      LEFT JOIN zentriq.departments dd ON dd.id = dir.department_id
                      LEFT JOIN zentriq.locations lo ON lo.id = dir.location_id
                     WHERE (@q::text IS NULL OR COALESCE(dir.name, it.person) ILIKE @q OR COALESCE(dir.email, it.email) ILIKE @q OR dir.employee_id ILIKE @q
                            OR COALESCE(dd.name, it.department) ILIKE @q OR dir.title ILIKE @q)
                       AND (@st::text IS NULL OR COALESCE(dir.status, 'active') = @st)
                       AND (NOT @holding OR COALESCE(it.assets, 0) + COALESCE(acc.accessories, 0) > 0)
                     LIMIT 5000) x
                """);
            cmd.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim() + "%"));
            cmd.Parameters.Add(T("st", status is "active" or "left" ? status : null));
            cmd.Parameters.AddWithValue("holding", holding == true);
            return Results.Ok(await JsonAsync(cmd, ct));
        });

        // Names for the "Assigned to" box: the people directory first, then logged-on users and accessory holders.
        g.MapGet("/people/suggest", async (NpgsqlDataSource ds, string? q, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT COALESCE(json_agg(x), '[]')::text FROM (
                    SELECT p.name, p.email, d.name AS department, p.employee_id AS "employeeId" FROM zentriq.lifecycle_people p
                      LEFT JOIN zentriq.departments d ON d.id = p.department_id
                     WHERE p.status = 'active' AND (@q::text IS NULL OR p.name ILIKE @q OR p.email ILIKE @q OR p.employee_id ILIKE @q)
                     ORDER BY lower(p.name) LIMIT 50) x
                """);
            cmd.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim() + "%"));
            return Results.Ok(await JsonAsync(cmd, ct));
        });

        async Task<IResult> SavePerson(Guid? id, PersonBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct)
        {
            var name = Clean(b.Name);
            if (name is null) return Validation.Problem("name", "Enter the name.");
            var email = Clean(b.Email, 254);
            if (email is not null && !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return Validation.Problem("email", "Enter a valid e-mail address.");
            if (b.Status is { } st && st is not ("active" or "left")) return Validation.Problem("status", "Status is Active or Left.");
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            string? oldName = null;
            if (id is not null)
            {
                await using var o = new NpgsqlCommand("SELECT name FROM zentriq.lifecycle_people WHERE id = @id", conn, tx);
                o.Parameters.AddWithValue("id", id.Value);
                oldName = await o.ExecuteScalarAsync(ct) as string;
                if (oldName is null) return Results.NotFound();
            }
            await using var cmd = new NpgsqlCommand(id is null ? """
                INSERT INTO zentriq.lifecycle_people (name, email, employee_id, title, phone, department_id, location_id, manager, status, notes, source, created_by)
                VALUES (@n, @e, @emp, @title, @phone, @dep, @loc, @mgr, COALESCE(@st, 'active'), @notes, 'manual', @by) RETURNING id
                """ : """
                UPDATE zentriq.lifecycle_people SET name = @n, email = @e, employee_id = @emp, title = @title, phone = @phone, department_id = @dep, location_id = @loc,
                       manager = @mgr, status = COALESCE(@st, status), notes = @notes, updated_at = now() WHERE id = @id RETURNING id
                """, conn, tx);
            cmd.Parameters.Add(U("id", id));
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.Add(T("e", email));
            cmd.Parameters.Add(T("emp", Clean(b.EmployeeId, 50)));
            cmd.Parameters.Add(T("title", Clean(b.Title)));
            cmd.Parameters.Add(T("phone", Clean(b.Phone, 50)));
            cmd.Parameters.Add(U("dep", b.DepartmentId));
            cmd.Parameters.Add(U("loc", b.LocationId));
            cmd.Parameters.Add(T("mgr", Clean(b.Manager)));
            cmd.Parameters.Add(T("st", b.Status));
            cmd.Parameters.Add(T("notes", Clean(b.Notes, 4000)));
            cmd.Parameters.Add(T("by", rc.UserName));
            Guid newId;
            try { newId = (Guid)(await cmd.ExecuteScalarAsync(ct))!; }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Validation.Problem(ex.ConstraintName switch { "ux_lifecycle_people_email" => "email", "ux_lifecycle_people_empid" => "employeeId", _ => "name" },
                    ex.ConstraintName switch
                    {
                        "ux_lifecycle_people_email" => "Another person has this e-mail.",
                        "ux_lifecycle_people_empid" => "Another person has this employee ID.",
                        _ => "A person with this name is already in the directory.",
                    });
            }
            // Renaming a person renames their assignments (assignments are by name).
            if (oldName is not null && !string.Equals(oldName.Trim(), name, StringComparison.Ordinal))
            {
                await using var ren = new NpgsqlCommand("""
                    UPDATE zentriq.asset_lifecycle SET assigned_to = @n, assigned_email = COALESCE(@e, assigned_email), updated_at = now() WHERE lower(btrim(assigned_to)) = lower(btrim(@o));
                    UPDATE zentriq.assets a SET owner_name = @n FROM zentriq.asset_lifecycle l WHERE l.asset_id = a.id AND l.assigned_to = @n AND a.owner_name IS DISTINCT FROM @n;
                    """, conn, tx);
                ren.Parameters.AddWithValue("n", name);
                ren.Parameters.AddWithValue("o", oldName);
                ren.Parameters.Add(T("e", email));
                await ren.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            await rc.AuditAsync(id is null ? "lifecycle.person_create" : "lifecycle.person_update", name, detail: new { email, b.EmployeeId, b.Status });
            return Results.Ok(new { id = newId });
        }

        g.MapPost("/people", (PersonBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => SavePerson(null, b, ds, rc, ct))
            .RequireAuthorization(Permissions.LifecycleManage);
        g.MapPut("/people/{id:guid}", (Guid id, PersonBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => SavePerson(id, b, ds, rc, ct))
            .RequireAuthorization(Permissions.LifecycleManage);
        g.MapDelete("/people/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                DELETE FROM zentriq.lifecycle_people p WHERE p.id = @id
                   AND NOT EXISTS (SELECT 1 FROM zentriq.asset_lifecycle l WHERE lower(btrim(l.assigned_to)) = lower(btrim(p.name)) AND l.stage IN ('in_use','in_maintenance','missing'))
                RETURNING name
                """);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name)
                return Results.Problem(statusCode: 409, title: "This person still holds IT assets (or no longer exists). Check them in first, or mark the person as Left.");
            await rc.AuditAsync("lifecycle.person_delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.LifecycleManage);

        // ---- bulk upload: IT assets, people, contracts --------------------------------------------------------------
        g.MapGet("/import/{kind}/template", async (string kind, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (!LifecycleImport.Kinds.Contains(kind)) return Results.NotFound();
            await using var conn = await ds.OpenConnectionAsync(ct);
            var file = await LifecycleImport.TemplateAsync(conn, kind, ct);
            return Results.File(file, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Zentriq-{kind switch { "people" => "people", "contracts" => "contracts", _ => "IT-assets" }}-upload.xlsx");
        });

        g.MapPost("/import/{kind}", async (string kind, HttpContext http, NpgsqlDataSource ds, RequestContext rc, bool? dryRun, bool? update, bool? createMissing, string? name, CancellationToken ct) =>
        {
            if (!LifecycleImport.Kinds.Contains(kind)) return Results.NotFound();
            if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = 25 * 1024 * 1024;
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length == 0) return Validation.Problem("file", "The file is empty.");
            List<string[]> rows;
            try { rows = Zentriq.Infrastructure.NonIt.SheetReader.Read(ms.ToArray(), kind switch { "people" => "People", "contracts" => "Contracts", _ => "Assets" }); }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
            {
                return Validation.Problem("file", "The file could not be read: " + ex.Message);
            }
            await using var conn = await ds.OpenConnectionAsync(ct);
            var result = await LifecycleImport.RunAsync(conn, kind, rows, new LcImportOptions(dryRun != false, update != false, createMissing != false), rc.UserName, ct);
            if (!result.DryRun && result.Ok)
                await rc.AuditAsync("lifecycle.import", $"{LifecycleImport.Title(kind)}: {name ?? "upload"}", detail: new { kind, result.Rows, result.Created, result.Updated, result.Skipped });
            return Results.Ok(result);
        }).RequireAuthorization(Permissions.LifecycleManage).DisableAntiforgery();

        // ---- custom fields on IT assets -----------------------------------------------------------------------------
        async Task<IResult> SaveField(Guid? id, FieldBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct)
        {
            var label = Clean(b.Label, 60);
            if (label is null) return Validation.Problem("label", "Enter the field name.");
            if (!LifecycleFields.Types.Contains(b.Type)) return Validation.Problem("type", "Type is text, number, date, list or yes / no.");
            var options = (b.Options ?? []).Select(o => o.Trim()).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
            if (b.Type == "list" && options.Length == 0) return Validation.Problem("options", "Enter the values of the list.");
            await using var cmd = ds.CreateCommand(id is null ? """
                INSERT INTO zentriq.lifecycle_fields (key, label, type, options, required, sort)
                VALUES (@k, @l, @t, @o, @r, COALESCE(@s, (SELECT COALESCE(max(sort), 0) + 10 FROM zentriq.lifecycle_fields))) RETURNING id
                """ : """
                UPDATE zentriq.lifecycle_fields SET label = @l, type = @t, options = @o, required = @r, sort = COALESCE(@s, sort) WHERE id = @id RETURNING id
                """);
            cmd.Parameters.Add(U("id", id));
            cmd.Parameters.AddWithValue("k", LifecycleFields.KeyOf(label));
            cmd.Parameters.AddWithValue("l", label);
            cmd.Parameters.AddWithValue("t", b.Type);
            cmd.Parameters.Add(new NpgsqlParameter("o", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = options });
            cmd.Parameters.AddWithValue("r", b.Required == true);
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Integer) { Value = (object?)b.Sort ?? DBNull.Value });
            try
            {
                if (await cmd.ExecuteScalarAsync(ct) is not Guid fid) return Results.NotFound();
                await rc.AuditAsync(id is null ? "lifecycle.field_create" : "lifecycle.field_update", label, detail: new { b.Type, options });
                return Results.Ok(new { id = fid });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("label", "A field with this name already exists."); }
        }
        g.MapPost("/fields", (FieldBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => SaveField(null, b, ds, rc, ct)).RequireAuthorization(Permissions.SettingsManage);
        g.MapPut("/fields/{id:guid}", (Guid id, FieldBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => SaveField(id, b, ds, rc, ct)).RequireAuthorization(Permissions.SettingsManage);
        g.MapDelete("/fields/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                WITH d AS (DELETE FROM zentriq.lifecycle_fields WHERE id = @id RETURNING key, label)
                UPDATE zentriq.asset_lifecycle SET custom = custom - (SELECT key FROM d) WHERE custom ? (SELECT key FROM d)
                RETURNING (SELECT label FROM d)
                """);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("lifecycle.field_delete", id.ToString());
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SettingsManage);

        // Everything one person holds (IT assets and returnable accessories): handover / return form and offboarding.
        g.MapGet("/people/holdings", async (string person, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($$"""
                SELECT json_build_object(
                  'assets', (SELECT COALESCE(json_agg(x ORDER BY x."assetTag" NULLS LAST), '[]') FROM (SELECT {{Cols}} FROM {{From}}
                               WHERE lower(v.assigned_to) = lower(@p) AND v.stage IN ('in_use','in_maintenance','missing')) x),
                  'accessories', (SELECT COALESCE(json_agg(x ORDER BY x.item), '[]') FROM (
                        SELECT i.name AS item, i.category, sum(CASE m.kind WHEN 'issue' THEN -m.quantity ELSE -(m.quantity + m.faulty_change) END) AS quantity,
                               max(m.occurred_at) FILTER (WHERE m.kind = 'issue') AS "lastIssued",
                               (SELECT array_agg(u.serial ORDER BY u.serial) FROM zentriq.accessory_units u WHERE u.item_id = i.id AND u.person_key = m.person_key AND u.status = 'issued') AS serials
                          FROM zentriq.accessory_movements m JOIN zentriq.accessory_items i ON i.id = m.item_id
                         WHERE m.kind IN ('issue','return') AND m.person_key = regexp_replace(lower(trim(@p)), '\s+', ' ', 'g') AND i.returnable
                         GROUP BY i.id, i.name, i.category, m.person_key HAVING sum(CASE m.kind WHEN 'issue' THEN -m.quantity ELSE -(m.quantity + m.faulty_change) END) > 0) x))::text
                """);
            cmd.Parameters.AddWithValue("p", person.Trim());
            return Results.Ok(await JsonAsync(cmd, ct));
        });

        // ---- contracts ------------------------------------------------------------------------------------------
        g.MapGet("/contracts", async (NpgsqlDataSource ds, string? q, string? kind, bool? expiring, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT COALESCE(json_agg(x ORDER BY x."endDate" NULLS LAST, x.name), '[]')::text FROM (
                    SELECT c.id, c.name, c.kind, c.vendor, c.reference, c.start_date AS "startDate", c.end_date AS "endDate", c.cost, c.currency,
                           c.notify_days AS "notifyDays", c.notes, c.created_at AS "createdAt", c.created_by AS "createdBy",
                           (c.end_date - current_date) AS "daysLeft",
                           (SELECT count(*) FROM zentriq.asset_contract_assets ca WHERE ca.contract_id = c.id) AS assets
                      FROM zentriq.asset_contracts c
                     WHERE (@k::text IS NULL OR c.kind = @k) AND (NOT @exp OR c.end_date BETWEEN current_date AND current_date + 90)
                       AND (@q::text IS NULL OR c.name ILIKE @q OR c.vendor ILIKE @q OR c.reference ILIKE @q)) x
                """);
            cmd.Parameters.Add(T("k", kind));
            cmd.Parameters.AddWithValue("exp", expiring == true);
            cmd.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim() + "%"));
            return Results.Ok(await JsonAsync(cmd, ct));
        });

        g.MapGet("/contracts/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($$"""
                SELECT json_build_object(
                  'contract', (SELECT row_to_json(x) FROM (SELECT c.id, c.name, c.kind, c.vendor, c.reference, c.start_date AS "startDate", c.end_date AS "endDate",
                                c.cost, c.currency, c.notify_days AS "notifyDays", c.notes FROM zentriq.asset_contracts c WHERE c.id = @id) x),
                  'assets', (SELECT COALESCE(json_agg(x), '[]') FROM (SELECT {{Cols}} FROM {{From}} JOIN zentriq.asset_contract_assets ca ON ca.asset_id = v.asset_id
                              WHERE ca.contract_id = @id ORDER BY lower(v.asset_tag) NULLS LAST) x))::text
                """);
            cmd.Parameters.AddWithValue("id", id);
            var r = await JsonAsync(cmd, ct);
            return r.GetProperty("contract").ValueKind == JsonValueKind.Null ? Results.NotFound() : Results.Ok(r);
        });

        async Task<IResult> SaveContract(Guid? id, ContractBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct)
        {
            var name = Clean(b.Name);
            if (name is null) return Validation.Problem("name", "Enter a name.");
            if (!new[] { "warranty", "amc", "support", "lease", "insurance", "other" }.Contains(b.Kind)) return Validation.Problem("kind", "Unknown contract type.");
            if (b.StartDate is { } s && b.EndDate is { } e && e < s) return Validation.Problem("endDate", "The contract ends before it starts.");
            if (b.Cost is < 0) return Validation.Problem("cost", "The cost cannot be negative.");
            if (b.NotifyDays is < 0 or > 365) return Validation.Problem("notifyDays", "Remind between 0 and 365 days before the end.");
            await using var cmd = ds.CreateCommand(id is null ? """
                INSERT INTO zentriq.asset_contracts (name, kind, vendor, reference, start_date, end_date, cost, currency, notify_days, notes, created_by)
                VALUES (@n, @k, @v, @r, @s, @e, @c, @cur, @nd, @notes, @by) RETURNING id
                """ : """
                UPDATE zentriq.asset_contracts SET name = @n, kind = @k, vendor = @v, reference = @r, start_date = @s, end_date = @e, cost = @c, currency = @cur,
                       notify_days = @nd, notes = @notes, updated_at = now() WHERE id = @id RETURNING id
                """);
            cmd.Parameters.Add(U("id", id));
            cmd.Parameters.Add(T("n", name));
            cmd.Parameters.AddWithValue("k", b.Kind);
            cmd.Parameters.Add(T("v", Clean(b.Vendor)));
            cmd.Parameters.Add(T("r", Clean(b.Reference, 100)));
            cmd.Parameters.Add(D("s", b.StartDate));
            cmd.Parameters.Add(D("e", b.EndDate));
            cmd.Parameters.Add(N("c", b.Cost));
            cmd.Parameters.Add(T("cur", Clean(b.Currency, 3)?.ToUpperInvariant()));
            cmd.Parameters.AddWithValue("nd", b.NotifyDays ?? 90);
            cmd.Parameters.Add(T("notes", Clean(b.Notes, 4000)));
            cmd.Parameters.Add(T("by", rc.UserName));
            if (await cmd.ExecuteScalarAsync(ct) is not Guid saved) return Results.NotFound();
            await rc.AuditAsync(id is null ? "lifecycle.contract.create" : "lifecycle.contract.update", name, detail: new { id = saved, b.Kind, b.EndDate });
            return Results.Ok(new { id = saved });
        }
        g.MapPost("/contracts", (ContractBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => SaveContract(null, b, ds, rc, ct))
            .RequireAuthorization(Permissions.LifecycleManage);
        g.MapPut("/contracts/{id:guid}", (Guid id, ContractBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => SaveContract(id, b, ds, rc, ct))
            .RequireAuthorization(Permissions.LifecycleManage);
        g.MapDelete("/contracts/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.asset_contracts WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("lifecycle.contract.delete", name, detail: new { id });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.LifecycleManage);

        g.MapPost("/contracts/{id:guid}/assets", async (Guid id, ContractAssetsBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            string? name;
            await using (var c = new NpgsqlCommand("SELECT name FROM zentriq.asset_contracts WHERE id = @id FOR UPDATE", conn, tx))
            {
                c.Parameters.AddWithValue("id", id);
                name = await c.ExecuteScalarAsync(ct) as string;
            }
            if (name is null) return Results.NotFound();
            int added = 0, removed = 0;
            if (b.Add is { Length: > 0 })
            {
                await using var add = new NpgsqlCommand("""
                    INSERT INTO zentriq.asset_contract_assets (contract_id, asset_id)
                    SELECT @id, a FROM unnest(@a) AS a WHERE EXISTS (SELECT 1 FROM zentriq.asset_lifecycle l WHERE l.asset_id = a) ON CONFLICT DO NOTHING
                    """, conn, tx);
                add.Parameters.AddWithValue("id", id);
                add.Parameters.AddWithValue("a", b.Add.Distinct().Take(5000).ToArray());
                added = await add.ExecuteNonQueryAsync(ct);
            }
            if (b.Remove is { Length: > 0 })
            {
                await using var rem = new NpgsqlCommand("DELETE FROM zentriq.asset_contract_assets WHERE contract_id = @id AND asset_id = ANY(@a)", conn, tx);
                rem.Parameters.AddWithValue("id", id);
                rem.Parameters.AddWithValue("a", b.Remove.Distinct().ToArray());
                removed = await rem.ExecuteNonQueryAsync(ct);
            }
            await using (var ev = new NpgsqlCommand("""
                INSERT INTO zentriq.asset_lifecycle_events (asset_id, actor, kind, reason, detail)
                SELECT a, @by, 'contract', CASE WHEN a = ANY(@add) THEN 'Added to contract ' ELSE 'Removed from contract ' END || @n, jsonb_build_object('contractId', @id)
                  FROM unnest(@all) AS a WHERE EXISTS (SELECT 1 FROM zentriq.asset_lifecycle l WHERE l.asset_id = a)
                """, conn, tx))
            {
                ev.Parameters.AddWithValue("by", rc.UserName);
                ev.Parameters.AddWithValue("n", name);
                ev.Parameters.AddWithValue("id", id);
                ev.Parameters.AddWithValue("add", (b.Add ?? []).Distinct().ToArray());
                ev.Parameters.AddWithValue("all", (b.Add ?? []).Concat(b.Remove ?? []).Distinct().Take(5000).ToArray());
                await ev.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            await rc.AuditAsync("lifecycle.contract.assets", name, detail: new { id, added, removed });
            return Results.Ok(new { added, removed });
        }).RequireAuthorization(Permissions.LifecycleManage);

        // ---- refresh planning -----------------------------------------------------------------------------------
        g.MapGet("/refresh", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($$"""
                WITH c AS (
                    SELECT v.*, {{OsEndOfSupport}} AS os_eos,
                           (COALESCE(v.purchase_date, v.in_service_date, v.first_seen_at::date) + make_interval(months => (v.refresh_years * 12)::int))::date AS due_on
                      FROM zentriq.v_asset_lifecycle v WHERE v.stage IN ('in_use','in_stock','in_maintenance') AND v.class <> 'virtual'),
                avgcost AS (SELECT class, round(avg(purchase_cost), 2) AS cost FROM zentriq.v_asset_lifecycle WHERE purchase_cost > 0 GROUP BY class)
                SELECT json_build_object(
                  'candidates', (SELECT COALESCE(json_agg(x ORDER BY x."dueOn"), '[]') FROM (
                        SELECT c.asset_id AS "id", c.asset_tag AS "assetTag", COALESCE(c.hostname, c.serial, c.model) AS "name", c.model, c.class_label AS "classLabel",
                               c.assigned_to AS "assignedTo", c.os_name AS "osName", c.age_months AS "ageMonths", c.warranty_end AS "warrantyEnd", c.due_on AS "dueOn",
                               c.purchase_cost AS "purchaseCost", c.book_value AS "bookValue",
                               array_remove(ARRAY[CASE WHEN c.refresh_due THEN 'age' END, CASE WHEN c.warranty_days < 0 THEN 'warranty' END,
                                                  CASE WHEN c.os_eos THEN 'os' END], NULL) AS reasons
                          FROM c WHERE c.refresh_due OR c.os_eos OR (c.warranty_days < 0 AND c.refresh_years > 0 AND c.due_on <= current_date + 365)) x),
                  'forecast', (SELECT COALESCE(json_agg(x ORDER BY x.year), '[]') FROM (
                        SELECT GREATEST(extract(year FROM c.due_on)::int, extract(year FROM current_date)::int) AS year, count(*) AS devices,
                               COALESCE(sum(COALESCE(c.purchase_cost, a.cost)), 0) AS "estimatedCost",
                               count(*) FILTER (WHERE c.purchase_cost IS NULL AND a.cost IS NULL) AS "withoutCost"
                          FROM c LEFT JOIN avgcost a ON a.class = c.class
                         WHERE c.refresh_years > 0 AND c.due_on < make_date(extract(year FROM current_date)::int + 5, 1, 1)
                         GROUP BY 1) x))::text
                """);
            return Results.Ok(await JsonAsync(cmd, ct));
        });

        // ---- history --------------------------------------------------------------------------------------------
        g.MapGet("/events", async (NpgsqlDataSource ds, string? kind, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, size) = Paging.Normalize(page, pageSize);
            await using var cmd = ds.CreateCommand("""
                SELECT json_build_object('items', COALESCE(json_agg(x), '[]'), 'total', COALESCE(max(x.total), 0))::text FROM (
                    SELECT e.id, e.at, e.actor, e.kind, e.from_stage AS "fromStage", e.to_stage AS "toStage", e.from_status AS "fromStatus", e.to_status AS "toStatus",
                           e.person, e.reason, e.asset_id AS "assetId", l.asset_tag AS "assetTag", COALESCE(a.hostname, a.serial_number, a.manual_serial) AS "name",
                           count(*) OVER () AS total
                      FROM zentriq.asset_lifecycle_events e JOIN zentriq.assets a ON a.id = e.asset_id JOIN zentriq.asset_lifecycle l ON l.asset_id = a.id
                     WHERE @k::text IS NULL OR e.kind = @k
                     ORDER BY e.id DESC LIMIT @l OFFSET @o) x
                """);
            cmd.Parameters.Add(T("k", kind));
            cmd.Parameters.AddWithValue("l", size);
            cmd.Parameters.AddWithValue("o", (p - 1) * size);
            return Results.Ok(await JsonAsync(cmd, ct));
        });

        // ---- policies and settings ------------------------------------------------------------------------------
        g.MapPut("/policies/{cls}", async (string cls, PolicyBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (!LifecycleStore.Classes.Contains(cls)) return Results.NotFound();
            var prefix = (b.TagPrefix ?? "").Trim().ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(prefix, "^[A-Z0-9]{1,8}$")) return Validation.Problem("tagPrefix", "Tag prefixes are 1 to 8 letters or digits.");
            if (!LifecycleStore.DepreciationMethods.Contains(b.DepreciationMethod)) return Validation.Problem("depreciationMethod", "Unknown depreciation method.");
            if (b.UsefulLifeMonths is < 0 or > 600) return Validation.Problem("usefulLifeMonths", "Useful life must be between 0 and 600 months.");
            if (b.SalvagePercent is < 0 or > 100) return Validation.Problem("salvagePercent", "Salvage value is 0 to 100 % of the cost.");
            if (b.RefreshYears is < 0 or > 30) return Validation.Problem("refreshYears", "Refresh age is 0 to 30 years.");
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.lifecycle_policies SET tag_prefix = @p, useful_life_months = @l, depreciation_method = @m, salvage_percent = @s, refresh_years = @r, updated_at = now()
                 WHERE class = @c
                """);
            cmd.Parameters.AddWithValue("c", cls);
            cmd.Parameters.AddWithValue("p", prefix);
            cmd.Parameters.AddWithValue("l", b.UsefulLifeMonths);
            cmd.Parameters.AddWithValue("m", b.DepreciationMethod);
            cmd.Parameters.AddWithValue("s", b.SalvagePercent);
            cmd.Parameters.AddWithValue("r", b.RefreshYears);
            await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("lifecycle.policy", cls, detail: b);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapPut("/settings", async (SettingsBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (b.Currency is { } c && !System.Text.RegularExpressions.Regex.IsMatch(c.Trim(), "^[A-Za-z]{3}$")) return Validation.Problem("currency", "Use a three-letter currency code (INR, USD, EUR…).");
            if (b.NotSeenDays is < 0 or > 3650) return Validation.Problem("notSeenDays", "Enter 0 (off) to 3650 days.");
            if (b.TagMode is { } mode && mode is not ("auto" or "manual")) return Validation.Problem("tagMode", "Choose automatic or manual tags.");
            if (b.TagFormat is { } fmt && TagFormat.Validate(fmt.Trim()) is { } fmtError) return Validation.Problem("tagFormat", fmtError);
            if (b.TagOrg is { } org && !System.Text.RegularExpressions.Regex.IsMatch(org.Trim(), "^[A-Za-z0-9]{0,10}$")) return Validation.Problem("tagOrg", "The organisation code is up to 10 letters and digits.");
            if (b.TagStart is < 1 or > 99_999_999) return Validation.Problem("tagStart", "Start numbers at 1 to 99,999,999.");
            await using var conn = await ds.OpenConnectionAsync(ct);
            async Task Set(string key, string json)
            {
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO zentriq.system_settings (key, value, updated_at, updated_by) VALUES (@k, @v::jsonb, now(), @u)
                    ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now(), updated_by = EXCLUDED.updated_by
                    """, conn);
                cmd.Parameters.AddWithValue("k", key);
                cmd.Parameters.AddWithValue("v", json);
                cmd.Parameters.Add(U("u", rc.UserId));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (b.Currency is { } cur) await Set("lifecycle.currency", JsonSerializer.Serialize(cur.Trim().ToUpperInvariant()));
            if (b.NotSeenDays is { } d) await Set("lifecycle.not_seen_days", d.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (b.AutoTag is { } t) { await Set("lifecycle.auto_tag", t ? "true" : "false"); if (b.TagMode is null) await Set("lifecycle.tag_mode", t ? "\"auto\"" : "\"manual\""); }
            if (b.TagMode is { } tm) { await Set("lifecycle.tag_mode", JsonSerializer.Serialize(tm)); await Set("lifecycle.auto_tag", tm == "auto" ? "true" : "false"); }
            if (b.TagFormat is { } tf) await Set("lifecycle.tag_format", JsonSerializer.Serialize(tf.Trim()));
            if (b.TagOrg is { } to) await Set("lifecycle.tag_org", JsonSerializer.Serialize(to.Trim().ToUpperInvariant()));
            if (b.TagStart is { } tst) await Set("lifecycle.tag_start", tst.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (b.TagDiscovered is { } td) await Set("lifecycle.tag_discovered", td ? "true" : "false");
            await rc.AuditAsync("lifecycle.settings", null, detail: b);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(Permissions.SettingsManage);
    }
}
