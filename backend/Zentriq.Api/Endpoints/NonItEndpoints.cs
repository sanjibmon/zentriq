using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.NonIt;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Non-IT Asset Inventory: furniture, appliances, vehicles, tools, safety equipment and other items an organisation owns,
/// kept by hand, by barcode or from an Excel form. Built-in and custom categories, custom fields, history per asset.
/// </summary>
public static class NonItEndpoints
{
    public sealed record IdsBody(Guid[]? Ids);
    public sealed record BulkBody(Guid[]? Ids, string? Status, string? Location, Guid? DepartmentId, bool ClearDepartment, string? AssignedTo, string? Condition, Guid? CategoryId);
    public sealed record CategoryBody(string? Name, string? Group);
    public sealed record FieldBody(string? Key, string? Label, string? Type, string[]? Options, int? Sort);

    private const string Select = $"""
        SELECT n.id AS "id", n.asset_tag AS "assetTag", n.barcode AS "barcode", n.name AS "name", n.category_id AS "categoryId",
               c.name AS "category", c.group_name AS "group", n.manufacturer AS "manufacturer", n.model AS "model", n.serial_number AS "serialNumber",
               n.status AS "status", n.condition AS "condition", n.quantity AS "quantity", n.location AS "location", n.department_id AS "departmentId",
               d.name AS "department", n.assigned_to AS "assignedTo", n.purchase_date AS "purchaseDate", n.vendor AS "vendor", n.purchase_cost AS "purchaseCost",
               n.invoice_number AS "invoiceNumber", n.po_number AS "poNumber", n.warranty_until AS "warrantyUntil", n.amc_vendor AS "amcVendor",
               n.amc_until AS "amcUntil", n.insurance_until AS "insuranceUntil", n.last_maintenance AS "lastMaintenance", n.next_maintenance AS "nextMaintenance",
               n.useful_life_years AS "usefulLifeYears", n.salvage_value AS "salvageValue", n.disposed_on AS "disposedOn", n.notes AS "notes", n.custom AS "custom",
               {NonItFields.CurrentValueSql} AS "currentValue",
               n.created_at AS "createdAt", n.created_by AS "createdBy", n.updated_at AS "updatedAt", n.updated_by AS "updatedBy", n.row_version AS "rowVersion"
          FROM zentriq.nonit_assets n
          LEFT JOIN zentriq.nonit_categories c ON c.id = n.category_id
          LEFT JOIN zentriq.departments d ON d.id = n.department_id
        """;

    private const string Where = """
         WHERE (@q::text IS NULL OR n.asset_tag ILIKE '%' || @q || '%' OR n.name ILIKE '%' || @q || '%' OR n.serial_number ILIKE '%' || @q || '%'
                OR n.barcode ILIKE '%' || @q || '%' OR n.model ILIKE '%' || @q || '%' OR n.manufacturer ILIKE '%' || @q || '%'
                OR n.assigned_to ILIKE '%' || @q || '%' OR n.location ILIKE '%' || @q || '%' OR c.name ILIKE '%' || @q || '%')
           AND (@cat::uuid IS NULL OR n.category_id = @cat)
           AND (@grp::text IS NULL OR c.group_name = @grp)
           AND (@st::text IS NULL OR n.status = @st OR (@st = 'active' AND n.status <> 'disposed'))
           AND (@dep::uuid IS NULL OR n.department_id = @dep)
           AND (@loc::text IS NULL OR n.location = @loc)
           AND (@due::text IS NULL
                OR (@due = 'warranty' AND n.warranty_until BETWEEN current_date AND current_date + 90)
                OR (@due = 'amc' AND n.amc_until BETWEEN current_date AND current_date + 90)
                OR (@due = 'insurance' AND n.insurance_until BETWEEN current_date AND current_date + 90)
                OR (@due = 'maintenance' AND n.next_maintenance <= current_date + 30 AND n.status <> 'disposed')
                OR (@due = 'overdue' AND n.next_maintenance < current_date AND n.status <> 'disposed')
                OR (@due = 'expired' AND (n.warranty_until < current_date OR n.amc_until < current_date) AND n.status <> 'disposed'))
        """;

    private static readonly Dictionary<string, string> Sorts = new()
    {
        ["tag"] = "lower(n.asset_tag)", ["name"] = "lower(n.name)", ["category"] = "lower(c.name)", ["status"] = "n.status", ["location"] = "lower(n.location)",
        ["department"] = "lower(d.name)", ["purchase"] = "n.purchase_date", ["cost"] = "n.purchase_cost", ["warranty"] = "n.warranty_until",
        ["maintenance"] = "n.next_maintenance", ["updated"] = "n.updated_at",
    };

    public static void MapNonItEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/nonit").WithTags("Non-IT assets").RequireModule(Modules.Inventory);

        g.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) => await Json(ds, $$"""
            WITH a AS (SELECT n.*, c.name AS cat, c.group_name AS grp, d.name AS dep, {{NonItFields.CurrentValueSql}} AS cur
                         FROM zentriq.nonit_assets n LEFT JOIN zentriq.nonit_categories c ON c.id = n.category_id
                         LEFT JOIN zentriq.departments d ON d.id = n.department_id),
                 live AS (SELECT * FROM a WHERE status <> 'disposed')
            SELECT jsonb_build_object(
              'total', (SELECT count(*) FROM live),
              'items', (SELECT COALESCE(sum(quantity), 0) FROM live),
              'disposed', (SELECT count(*) FROM a WHERE status = 'disposed'),
              'purchaseValue', (SELECT COALESCE(sum(purchase_cost * GREATEST(quantity, 1)), 0) FROM live),
              'currentValue', (SELECT COALESCE(sum(cur * GREATEST(quantity, 1)), 0) FROM live),
              'addedLast30', (SELECT count(*) FROM a WHERE created_at > now() - interval '30 days'),
              'warranty90', (SELECT count(*) FROM live WHERE warranty_until BETWEEN current_date AND current_date + 90),
              'amc90', (SELECT count(*) FROM live WHERE amc_until BETWEEN current_date AND current_date + 90),
              'insurance90', (SELECT count(*) FROM live WHERE insurance_until BETWEEN current_date AND current_date + 90),
              'maintenance30', (SELECT count(*) FROM live WHERE next_maintenance BETWEEN current_date AND current_date + 30),
              'maintenanceOverdue', (SELECT count(*) FROM live WHERE next_maintenance < current_date),
              'expired', (SELECT count(*) FROM live WHERE warranty_until < current_date OR amc_until < current_date),
              'byGroup', (SELECT COALESCE(jsonb_agg(jsonb_build_object('key', k, 'count', n, 'value', v) ORDER BY n DESC), '[]')
                            FROM (SELECT COALESCE(grp, 'Uncategorised') k, count(*) n, COALESCE(sum(purchase_cost * GREATEST(quantity, 1)), 0) v FROM live GROUP BY 1) x),
              'byCategory', (SELECT COALESCE(jsonb_agg(jsonb_build_object('key', k, 'id', id, 'count', n) ORDER BY n DESC), '[]')
                            FROM (SELECT COALESCE(cat, 'Uncategorised') k, category_id id, count(*) n FROM live GROUP BY 1, 2 ORDER BY 3 DESC LIMIT 12) x),
              'byStatus', (SELECT COALESCE(jsonb_agg(jsonb_build_object('key', status, 'count', n) ORDER BY n DESC), '[]') FROM (SELECT status, count(*) n FROM a GROUP BY 1) x),
              'byCondition', (SELECT COALESCE(jsonb_agg(jsonb_build_object('key', COALESCE(condition, 'unknown'), 'count', n) ORDER BY n DESC), '[]') FROM (SELECT condition, count(*) n FROM live GROUP BY 1) x),
              'byDepartment', (SELECT COALESCE(jsonb_agg(jsonb_build_object('key', k, 'count', n) ORDER BY n DESC), '[]') FROM (SELECT COALESCE(dep, 'Unassigned') k, count(*) n FROM live GROUP BY 1 ORDER BY 2 DESC LIMIT 10) x),
              'byLocation', (SELECT COALESCE(jsonb_agg(jsonb_build_object('key', k, 'count', n) ORDER BY n DESC), '[]') FROM (SELECT COALESCE(location, 'Not recorded') k, count(*) n FROM live GROUP BY 1 ORDER BY 2 DESC LIMIT 10) x),
              'upcoming', (SELECT COALESCE(jsonb_agg(u ORDER BY u->>'date'), '[]') FROM (
                  SELECT jsonb_build_object('id', id, 'assetTag', asset_tag, 'name', name, 'what', w, 'date', dt) u FROM (
                      SELECT id, asset_tag, name, 'Warranty ends' w, warranty_until dt FROM live WHERE warranty_until BETWEEN current_date AND current_date + 90
                      UNION ALL SELECT id, asset_tag, name, 'AMC ends', amc_until FROM live WHERE amc_until BETWEEN current_date AND current_date + 90
                      UNION ALL SELECT id, asset_tag, name, 'Insurance ends', insurance_until FROM live WHERE insurance_until BETWEEN current_date AND current_date + 90
                      UNION ALL SELECT id, asset_tag, name, 'Maintenance due', next_maintenance FROM live WHERE next_maintenance <= current_date + 30) z
                  ORDER BY dt LIMIT 12) y)
            )::text
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/assets", async (NpgsqlDataSource ds, string? q, Guid? category, string? group, string? status, Guid? department, string? location, string? due,
            string? sort, string? dir, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (pg, size) = Paging.Normalize(page, pageSize);
            var order = Sorts.TryGetValue(sort ?? "", out var col) ? col : "n.updated_at";
            var desc = sort is null ? "DESC" : string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            return await Json(ds, $"""
                SELECT jsonb_build_object(
                  'total', (SELECT count(*) FROM zentriq.nonit_assets n LEFT JOIN zentriq.nonit_categories c ON c.id = n.category_id
                            LEFT JOIN zentriq.departments d ON d.id = n.department_id {Where}),
                  'page', {pg}, 'pageSize', {size},
                  'items', COALESCE((SELECT jsonb_agg(to_jsonb(t)) FROM ({Select} {Where} ORDER BY {order} {desc} NULLS LAST, n.asset_tag LIMIT {size} OFFSET {(pg - 1) * size}) t), '[]'))::text
                """, c => Filters(c, q, category, group, status, department, location, due), ct);
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/assets/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) => await Json(ds, $"""
            SELECT (to_jsonb(t) || jsonb_build_object('history', COALESCE((SELECT jsonb_agg(jsonb_build_object('at', h.at, 'actor', h.actor, 'action', h.action, 'changes', h.changes) ORDER BY h.at DESC)
                                                                   FROM (SELECT * FROM zentriq.nonit_history WHERE asset_id = @id ORDER BY at DESC LIMIT 100) h), '[]')))::text
              FROM ({Select} WHERE n.id = @id) t
            """, c => c.Parameters.AddWithValue("id", id), ct, notFound: true)).RequireAuthorization(Permissions.AssetsView);

        // Barcode scanner / search box: the asset whose barcode, tag or serial number matches exactly.
        g.MapGet("/lookup", async (NpgsqlDataSource ds, string code, CancellationToken ct) => await Json(ds, $"""
            SELECT to_jsonb(t)::text FROM ({Select}
             WHERE n.barcode = @c OR lower(n.asset_tag) = lower(@c) OR lower(n.serial_number) = lower(@c)
             ORDER BY (n.barcode = @c) DESC LIMIT 1) t
            """, c => c.Parameters.AddWithValue("c", (code ?? "").Trim()), ct, notFound: true)).RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/assets", async (NonItValues body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
            await SaveAsync(ds, rc, null, body, ct)).RequireAuthorization(Permissions.AssetsManage);
        g.MapPut("/assets/{id:guid}", async (Guid id, NonItValues body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
            await SaveAsync(ds, rc, id, body, ct)).RequireAuthorization(Permissions.AssetsManage);

        g.MapPost("/assets/delete", async (IdsBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var ids = (body.Ids ?? []).Distinct().Take(10_000).ToArray();
            if (ids.Length == 0) return Validation.Problem("ids", "Select the assets to delete.");
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.nonit_assets WHERE id = ANY(@ids) RETURNING asset_tag");
            cmd.Parameters.AddWithValue("ids", ids);
            var tags = new List<string>();
            await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) tags.Add(r.GetString(0));
            await rc.AuditAsync("nonit.delete", tags.Count == 1 ? tags[0] : $"{tags.Count} non-IT assets", detail: new { tags = tags.Take(200) }, ct: ct);
            return Results.Ok(new { deleted = tags.Count });
        }).RequireAuthorization(Permissions.AssetsManage);

        // Bulk change: status, location, department, assignee, condition or category of several assets.
        g.MapPost("/assets/bulk", async (BulkBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var ids = (b.Ids ?? []).Distinct().Take(10_000).ToArray();
            if (ids.Length == 0) return Validation.Problem("ids", "Select the assets to change.");
            if (b.Status is not null && !NonItFields.Statuses.Contains(b.Status)) return Validation.Problem("status", "Choose a status from the list.");
            if (b.Condition is not null && !NonItFields.Conditions.Contains(b.Condition)) return Validation.Problem("condition", "Choose a condition from the list.");
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new NpgsqlCommand("""
                UPDATE zentriq.nonit_assets SET
                    status = COALESCE(@st, status),
                    disposed_on = CASE WHEN COALESCE(@st, status) = 'disposed' THEN COALESCE(disposed_on, current_date) ELSE NULL END,
                    location = COALESCE(@loc, location),
                    department_id = CASE WHEN @clr THEN NULL ELSE COALESCE(@dep, department_id) END,
                    assigned_to = COALESCE(@asg, assigned_to), condition = COALESCE(@co, condition), category_id = COALESCE(@cat, category_id),
                    updated_at = now(), updated_by = @u, row_version = row_version + 1
                 WHERE id = ANY(@ids)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("ids", ids);
                cmd.Parameters.Add(Text("st", b.Status)); cmd.Parameters.Add(Text("loc", Blank(b.Location))); cmd.Parameters.Add(Text("asg", Blank(b.AssignedTo)));
                cmd.Parameters.Add(Text("co", b.Condition)); cmd.Parameters.Add(Text("u", rc.UserName));
                cmd.Parameters.Add(new NpgsqlParameter("dep", NpgsqlDbType.Uuid) { Value = (object?)b.DepartmentId ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("cat", NpgsqlDbType.Uuid) { Value = (object?)b.CategoryId ?? DBNull.Value });
                cmd.Parameters.AddWithValue("clr", b.ClearDepartment);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            var changes = new Dictionary<string, object?>();
            if (b.Status is not null) changes["status"] = b.Status;
            if (Blank(b.Location) is { } l) changes["location"] = l;
            if (b.DepartmentId is not null || b.ClearDepartment) changes["department_id"] = b.DepartmentId;
            if (Blank(b.AssignedTo) is { } a) changes["assigned_to"] = a;
            if (b.Condition is not null) changes["condition"] = b.Condition;
            if (b.CategoryId is not null) changes["category_id"] = b.CategoryId;
            foreach (var id in ids) await NonItStore.AddHistoryAsync(conn, tx, id, rc.UserName, "bulk-updated", changes, ct);
            await tx.CommitAsync(ct);
            await rc.AuditAsync("nonit.bulk-update", $"{ids.Length} non-IT assets", detail: changes, ct: ct);
            return Results.Ok(new { updated = ids.Length });
        }).RequireAuthorization(Permissions.AssetsManage);

        // ---- Categories and custom fields ----
        g.MapGet("/categories", async (NpgsqlDataSource ds, CancellationToken ct) => await Json(ds, """
            SELECT COALESCE(jsonb_agg(jsonb_build_object('id', c.id, 'name', c.name, 'group', c.group_name, 'builtin', c.builtin,
                   'count', (SELECT count(*) FROM zentriq.nonit_assets n WHERE n.category_id = c.id)) ORDER BY c.group_name, c.name), '[]')::text
              FROM zentriq.nonit_categories c
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/categories", async (CategoryBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var name = Blank(b.Name);
            if (name is null || name.Length > 100) return Validation.Problem("name", "Enter a category name (up to 100 characters).");
            var group = Blank(b.Group) ?? "Custom";
            await using var cmd = ds.CreateCommand("INSERT INTO zentriq.nonit_categories (name, group_name) VALUES (@n, @g) ON CONFLICT DO NOTHING RETURNING id");
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("g", group.Length > 60 ? group[..60] : group);
            if (await cmd.ExecuteScalarAsync(ct) is not Guid id) return Validation.Problem("name", $"A category named \"{name}\" already exists.");
            await rc.AuditAsync("nonit.category.create", name, detail: new { group }, ct: ct);
            return Results.Ok(new { id, name, group, builtin = false, count = 0 });
        }).RequireAuthorization(Permissions.AssetsManage);

        g.MapPut("/categories/{id:guid}", async (Guid id, CategoryBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var name = Blank(b.Name);
            if (name is null || name.Length > 100) return Validation.Problem("name", "Enter a category name (up to 100 characters).");
            await using var cmd = ds.CreateCommand("UPDATE zentriq.nonit_categories SET name = @n, group_name = COALESCE(@g, group_name) WHERE id = @id");
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.Add(Text("g", Blank(b.Group)));
            try { if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", $"A category named \"{name}\" already exists."); }
            await rc.AuditAsync("nonit.category.update", name, ct: ct);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AssetsManage);

        g.MapDelete("/categories/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                DELETE FROM zentriq.nonit_categories c WHERE c.id = @id AND NOT EXISTS (SELECT 1 FROM zentriq.nonit_assets n WHERE n.category_id = c.id) RETURNING name
                """);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name)
                return Results.Problem(statusCode: 409, title: "The category is in use. Move its assets to another category first.");
            await rc.AuditAsync("nonit.category.delete", name, ct: ct);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AssetsManage);

        g.MapGet("/fields", async (NpgsqlDataSource ds, CancellationToken ct) => await Json(ds, """
            SELECT COALESCE(jsonb_agg(jsonb_build_object('id', id, 'key', key, 'label', label, 'type', type, 'options', options, 'sort', sort) ORDER BY sort, label), '[]')::text
              FROM zentriq.nonit_fields
            """, null, ct)).RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/fields", async (FieldBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => await SaveFieldAsync(ds, rc, null, b, ct))
            .RequireAuthorization(Permissions.AssetsManage);
        g.MapPut("/fields/{id:guid}", async (Guid id, FieldBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => await SaveFieldAsync(ds, rc, id, b, ct))
            .RequireAuthorization(Permissions.AssetsManage);
        g.MapDelete("/fields/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.nonit_fields WHERE id = @id RETURNING label");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string label) return Results.NotFound();
            await rc.AuditAsync("nonit.field.delete", label, ct: ct);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AssetsManage);

        // ---- Excel form, bulk upload, export ----
        g.MapGet("/template", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var lk = await NonItStore.LookupsAsync(conn, null, ct);
            return Results.File(NonItStore.Template(lk), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Zentriq-non-IT-assets-form.xlsx");
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/import", async (HttpContext http, NpgsqlDataSource ds, RequestContext rc, bool? dryRun, bool? update, bool? createCategories, string? name, CancellationToken ct) =>
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = 25 * 1024 * 1024;
            using var ms = new MemoryStream();
            await http.Request.Body.CopyToAsync(ms, ct);
            if (ms.Length == 0) return Validation.Problem("file", "The file is empty.");
            List<string[]> rows;
            try { rows = SheetReader.Read(ms.ToArray(), "Assets"); }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
            {
                return Validation.Problem("file", "The file could not be read: " + ex.Message);
            }
            await using var conn = await ds.OpenConnectionAsync(ct);
            var result = await NonItImporter.RunAsync(conn, rows, new ImportOptions(update != false, createCategories == true, dryRun != false), rc.UserName, ct);
            if (!result.DryRun)
                await rc.AuditAsync("nonit.import", name ?? "upload", detail: new { result.Rows, result.Created, result.Updated, result.Skipped, result.NewCategories }, ct: ct);
            return Results.Ok(result);
        }).RequireAuthorization(Permissions.AssetsManage).DisableAntiforgery();

        g.MapGet("/export", async (NpgsqlDataSource ds, string? format, string? q, Guid? category, string? group, string? status, Guid? department, string? location, string? due, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var lk = await NonItStore.LookupsAsync(conn, null, ct);
            var headings = NonItStore.Headings(lk);
            var rows = new List<object?[]>();
            await using (var cmd = new NpgsqlCommand($"""
                SELECT n.asset_tag, n.name, c.name, n.manufacturer, n.model, n.serial_number, n.barcode, n.status, n.condition, n.quantity, n.location, d.name,
                       n.assigned_to, n.purchase_date, n.vendor, n.purchase_cost, n.invoice_number, n.po_number, n.warranty_until, n.amc_vendor, n.amc_until,
                       n.insurance_until, n.last_maintenance, n.next_maintenance, n.useful_life_years, n.salvage_value, n.notes, n.custom::text
                  FROM zentriq.nonit_assets n LEFT JOIN zentriq.nonit_categories c ON c.id = n.category_id LEFT JOIN zentriq.departments d ON d.id = n.department_id
                {Where} ORDER BY lower(n.asset_tag) LIMIT 100000
                """, conn))
            {
                Filters(cmd, q, category, group, status, department, location, due);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var row = new object?[headings.Count];
                    for (var i = 0; i < 27; i++)
                    {
                        if (r.IsDBNull(i)) continue;
                        row[i] = i switch
                        {
                            7 => NonItFields.StatusLabel(r.GetString(i)),
                            8 => NonItFields.ConditionLabel(r.GetString(i)),
                            9 => r.GetInt32(i),
                            13 or 18 or 20 or 21 or 22 or 23 => r.GetFieldValue<DateOnly>(i),
                            15 or 24 or 25 => r.GetDecimal(i),
                            _ => r.GetString(i),
                        };
                    }
                    var custom = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(27)) ?? [];
                    for (var i = 27; i < headings.Count; i++) row[i] = custom.TryGetValue(headings[i].Key["custom.".Length..], out var cv) ? cv : null;
                    rows.Add(row);
                }
            }
            var stamp = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                sb.AppendLine(string.Join(",", headings.Select(h => Csv(h.Title.TrimEnd(' ', '*')))));
                foreach (var row in rows) sb.AppendLine(string.Join(",", row.Select(v => Csv(v switch
                {
                    null => "", DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), decimal m => m.ToString(CultureInfo.InvariantCulture),
                    _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
                }))));
                return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"non-IT-assets-{stamp}.csv");
            }
            var sheet = new XlsxSheet("Assets");
            sheet.Headings.AddRange(headings.Select(h => h.Title.TrimEnd(' ', '*')));
            sheet.Widths.AddRange(headings.Select(h => h.Key is "name" or "notes" ? 32.0 : 18.0));
            sheet.Rows.AddRange(rows);
            return Results.File(XlsxBook.Write(sheet), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"non-IT-assets-{stamp}.xlsx");
        }).RequireAuthorization(Permissions.AssetsView);
    }

    private static async Task<IResult> SaveAsync(NpgsqlDataSource ds, RequestContext rc, Guid? id, NonItValues body, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        if (id is { } existing)
        {
            await using var ex = new NpgsqlCommand("SELECT 1 FROM zentriq.nonit_assets WHERE id = @id", conn, tx);
            ex.Parameters.AddWithValue("id", existing);
            if (await ex.ExecuteScalarAsync(ct) is null) return Results.NotFound();
        }
        var lk = await NonItStore.LookupsAsync(conn, tx, ct);
        try
        {
            var saved = await NonItStore.SaveAsync(conn, tx, id, body, lk, rc.UserName, id is null ? "created" : "updated", ct);
            await tx.CommitAsync(ct);
            await rc.AuditAsync(id is null ? "nonit.create" : "nonit.update", body.Name, detail: new { id = saved, body.AssetTag }, ct: ct);
            return Results.Ok(new { id = saved });
        }
        catch (NonItValidationException ex)
        {
            return Results.ValidationProblem(ex.Errors.ToDictionary(kv => kv.Key, kv => new[] { kv.Value }));
        }
    }

    private static async Task<IResult> SaveFieldAsync(NpgsqlDataSource ds, RequestContext rc, Guid? id, FieldBody b, CancellationToken ct)
    {
        var label = Blank(b.Label);
        if (label is null || label.Length > 60) return Validation.Problem("label", "Enter a label (up to 60 characters).");
        var type = b.Type ?? "text";
        if (type is not ("text" or "number" or "date" or "yesno" or "list")) return Validation.Problem("type", "Choose text, number, date, yes/no or list.");
        var options = (b.Options ?? []).Select(o => o.Trim()).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
        if (type == "list" && options.Length == 0) return Validation.Problem("options", "Enter the choices of the list.");
        var key = Blank(b.Key) ?? new string(label.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        if (key.Length == 0 || !char.IsLetter(key[0])) key = "f_" + key;
        if (key.Length > 40) key = key[..40];
        await using var cmd = ds.CreateCommand(id is null
            ? "INSERT INTO zentriq.nonit_fields (key, label, type, options, sort) VALUES (@k, @l, @t, @o, @s) RETURNING id"
            : "UPDATE zentriq.nonit_fields SET label = @l, type = @t, options = @o, sort = @s WHERE id = @id RETURNING id");
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("l", label);
        cmd.Parameters.AddWithValue("t", type);
        cmd.Parameters.AddWithValue("o", options);
        cmd.Parameters.AddWithValue("s", b.Sort ?? 0);
        if (id is { } fid) cmd.Parameters.AddWithValue("id", fid);
        try
        {
            if (await cmd.ExecuteScalarAsync(ct) is not Guid saved) return Results.NotFound();
            await rc.AuditAsync(id is null ? "nonit.field.create" : "nonit.field.update", label, detail: new { key, type }, ct: ct);
            return Results.Ok(new { id = saved, key, label, type, options });
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation)
        {
            return Validation.Problem("label", "A field with this name already exists, or the name cannot be used as a key.");
        }
    }

    private static void Filters(NpgsqlCommand c, string? q, Guid? category, string? group, string? status, Guid? department, string? location, string? due)
    {
        c.Parameters.Add(Text("q", Blank(q)?.Replace("%", "").Replace("_", "\\_")));
        c.Parameters.Add(new NpgsqlParameter("cat", NpgsqlDbType.Uuid) { Value = (object?)category ?? DBNull.Value });
        c.Parameters.Add(Text("grp", Blank(group)));
        c.Parameters.Add(Text("st", Blank(status)));
        c.Parameters.Add(new NpgsqlParameter("dep", NpgsqlDbType.Uuid) { Value = (object?)department ?? DBNull.Value });
        c.Parameters.Add(Text("loc", Blank(location)));
        c.Parameters.Add(Text("due", Blank(due)));
    }

    private static async Task<IResult> Json(NpgsqlDataSource ds, string sql, Action<NpgsqlCommand>? bind, CancellationToken ct, bool notFound = false)
    {
        await using var cmd = ds.CreateCommand(sql);
        bind?.Invoke(cmd);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        if (json is null && notFound) return Results.NotFound();
        return Results.Text(json ?? "null", "application/json");
    }

    private static string Csv(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' && !decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static NpgsqlParameter Text(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = (object?)v ?? DBNull.Value };
}
