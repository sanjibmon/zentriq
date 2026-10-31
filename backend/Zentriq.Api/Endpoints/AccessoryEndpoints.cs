using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Accessories;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// IT accessories (mouse, keyboard, headset, docking station…): a catalogue, stock per location, and every change as a
/// movement — receive, issue to a person, return, transfer, write-off, stock count — with holdings per person and
/// low-stock alerts (integration event accessory.low).
/// </summary>
public static class AccessoryEndpoints
{
    public sealed record ItemBody(string Name, string Category, string? Brand, string? Model, string? Sku, decimal? UnitCost, int? ReorderLevel,
        bool? Returnable, bool? SerialTracked, string? Notes, bool? Active);
    public sealed record LineBody(Guid ItemId, int Quantity, decimal? UnitCost, string[]? Serials);
    public sealed record MovementBody(string Kind, Guid LocationId, LineBody[] Lines, Guid? ToLocationId, string? Person, Guid? DepartmentId, Guid? AssetId,
        string? Condition, string? Reason, string? Reference, string? Vendor, string? Note);

    public static void MapAccessoryEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/accessories").WithTags("IT accessories").RequireModule(Modules.Inventory).RequireAuthorization(Permissions.AccessoriesView);

        g.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT (SELECT count(*) FROM zentriq.accessory_items WHERE active),
                       (SELECT COALESCE(sum(quantity), 0) FROM zentriq.accessory_stock),
                       (SELECT COALESCE(sum(s.quantity * COALESCE(i.unit_cost, 0)), 0) FROM zentriq.accessory_stock s JOIN zentriq.accessory_items i ON i.id = s.item_id),
                       (SELECT count(*) FROM zentriq.accessory_stock s JOIN zentriq.accessory_items i ON i.id = s.item_id WHERE i.active AND i.reorder_level > 0 AND s.quantity <= i.reorder_level),
                       (SELECT COALESCE(sum(-quantity), 0) FROM zentriq.accessory_movements WHERE kind = 'issue' AND occurred_at > now() - interval '30 days'),
                       (SELECT COALESCE(sum(faulty), 0) FROM zentriq.accessory_stock),
                       (SELECT count(*) FROM zentriq.locations),
                       (SELECT COALESCE(sum(h), 0) FROM (SELECT sum(CASE m.kind WHEN 'issue' THEN -m.quantity ELSE -(m.quantity + m.faulty_change) END) AS h
                          FROM zentriq.accessory_movements m JOIN zentriq.accessory_items i ON i.id = m.item_id
                         WHERE m.kind IN ('issue','return') AND m.person_key IS NOT NULL AND i.returnable GROUP BY m.person_key, m.item_id) x WHERE h > 0)
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return Results.Ok(new
            {
                items = r.GetInt64(0), inStock = Convert.ToInt64(r.GetValue(1)), stockValue = r.GetDecimal(2), lowStock = r.GetInt64(3),
                issued30d = Convert.ToInt64(r.GetValue(4)), faulty = Convert.ToInt64(r.GetValue(5)), locations = r.GetInt64(6), outstanding = Convert.ToInt64(r.GetValue(7)),
                categories = await CategoryNamesAsync(ds, ct),
            });
        });

        // ---- categories (a managed list: built-in ones plus those administrators add) ------------------------------
        g.MapGet("/categories", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT c.id, c.name, c.built_in, (SELECT count(*) FROM zentriq.accessory_items i WHERE lower(i.category) = lower(c.name))
                  FROM zentriq.accessory_categories c ORDER BY lower(c.name)
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<object>();
            while (await r.ReadAsync(ct)) list.Add(new { id = r.GetGuid(0), name = r.GetString(1), builtIn = r.GetBoolean(2), items = r.GetInt64(3) });
            return Results.Ok(list);
        });

        g.MapPost("/categories", async (CategoryBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var name = body.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 60) return Validation.Problem("name", "Enter the category name (up to 60 characters).");
            await using var cmd = ds.CreateCommand("INSERT INTO zentriq.accessory_categories (name) VALUES (@n) ON CONFLICT DO NOTHING RETURNING id");
            cmd.Parameters.AddWithValue("n", name);
            if (await cmd.ExecuteScalarAsync(ct) is not Guid id) return Validation.Problem("name", "This category already exists.");
            await rc.AuditAsync("accessory.category_create", name);
            return Results.Created($"/api/v1/accessories/categories/{id}", new { id, name });
        }).RequireAuthorization(Permissions.AccessoriesManage);

        g.MapPut("/categories/{id:guid}", async (Guid id, CategoryBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var name = body.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 60) return Validation.Problem("name", "Enter the category name (up to 60 characters).");
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            string? old;
            await using (var get = new NpgsqlCommand("SELECT name FROM zentriq.accessory_categories WHERE id = @id FOR UPDATE", conn, tx))
            {
                get.Parameters.AddWithValue("id", id);
                old = await get.ExecuteScalarAsync(ct) as string;
            }
            if (old is null) return Results.NotFound();
            try
            {
                await using var upd = new NpgsqlCommand("UPDATE zentriq.accessory_categories SET name = @n WHERE id = @id", conn, tx);
                upd.Parameters.AddWithValue("n", name); upd.Parameters.AddWithValue("id", id);
                await upd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "Another category has this name."); }
            // The accessories of the category move with it.
            await using (var items = new NpgsqlCommand("UPDATE zentriq.accessory_items SET category = @n, updated_at = now() WHERE lower(category) = lower(@o)", conn, tx))
            {
                items.Parameters.AddWithValue("n", name); items.Parameters.AddWithValue("o", old);
                await items.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            await rc.AuditAsync("accessory.category_rename", name, detail: new { from = old });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AccessoriesManage);

        g.MapDelete("/categories/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                DELETE FROM zentriq.accessory_categories c WHERE c.id = @id
                   AND NOT EXISTS (SELECT 1 FROM zentriq.accessory_items i WHERE lower(i.category) = lower(c.name)) RETURNING name
                """);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is string name) { await rc.AuditAsync("accessory.category_delete", name); return Results.NoContent(); }
            await using var ex = ds.CreateCommand("SELECT 1 FROM zentriq.accessory_categories WHERE id = @id");
            ex.Parameters.AddWithValue("id", id);
            return await ex.ExecuteScalarAsync(ct) is null ? Results.NotFound()
                : Results.Problem(statusCode: 409, title: "Accessories use this category. Move them to another category first, or rename the category.");
        }).RequireAuthorization(Permissions.AccessoriesManage);

        // ---- catalogue ---------------------------------------------------------------------------------------
        g.MapGet("/items", async (NpgsqlDataSource ds, string? q, string? category, bool? all, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT i.id, i.name, i.category, i.brand, i.model, i.sku, i.unit_cost, i.reorder_level, i.returnable, i.serial_tracked, i.notes, i.active,
                       COALESCE(sum(s.quantity), 0), COALESCE(sum(s.faulty), 0),
                       COALESCE(bool_or(i.reorder_level > 0 AND s.quantity <= i.reorder_level), false),
                       COALESCE(jsonb_agg(jsonb_build_object('locationId', s.location_id, 'location', lo.name, 'quantity', s.quantity, 'faulty', s.faulty,
                                          'low', i.reorder_level > 0 AND s.quantity <= i.reorder_level) ORDER BY lo.name) FILTER (WHERE s.location_id IS NOT NULL), '[]'),
                       (SELECT count(*) FROM zentriq.accessory_movements m WHERE m.item_id = i.id)
                  FROM zentriq.accessory_items i
                  LEFT JOIN zentriq.accessory_stock s ON s.item_id = i.id
                  LEFT JOIN zentriq.locations lo ON lo.id = s.location_id
                 WHERE (@all OR i.active) AND (@cat::text IS NULL OR i.category = @cat)
                   AND (@q::text IS NULL OR i.name ILIKE @q OR i.brand ILIKE @q OR i.model ILIKE @q OR i.sku ILIKE @q OR i.category ILIKE @q)
                 GROUP BY i.id ORDER BY i.category, lower(i.name)
                """);
            cmd.Parameters.AddWithValue("all", all == true);
            cmd.Parameters.Add(new NpgsqlParameter("cat", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(category) ? DBNull.Value : category });
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : "%" + q.Trim() + "%" });
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), category = r.GetString(2), brand = S(3), model = S(4), sku = S(5),
                    unitCost = r.IsDBNull(6) ? (decimal?)null : r.GetDecimal(6), reorderLevel = r.GetInt32(7), returnable = r.GetBoolean(8), serialTracked = r.GetBoolean(9),
                    notes = S(10), active = r.GetBoolean(11), quantity = Convert.ToInt64(r.GetValue(12)), faulty = Convert.ToInt64(r.GetValue(13)), low = r.GetBoolean(14),
                    locations = System.Text.Json.JsonDocument.Parse(r.GetString(15)).RootElement, movements = r.GetInt64(16),
                });
            }
            return Results.Ok(list);
        });

        g.MapPost("/items", async (ItemBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (Check(body) is { } problem) return problem;
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.accessory_items (id, name, category, brand, model, sku, unit_cost, reorder_level, returnable, serial_tracked, notes)
                VALUES (@id, @n, @c, @b, @m, @sku, @uc, @rl, @ret, @ser, @notes)
                """);
            Fill(cmd, id, body);
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem(ex.ConstraintName?.Contains("sku") == true ? "sku" : "name", ex.ConstraintName?.Contains("sku") == true ? "Another accessory has this SKU / part number." : "An accessory with this name already exists."); }
            await EnsureCategoryAsync(ds, body.Category!, ct);
            await rc.AuditAsync("accessory.item_create", body.Name.Trim());
            return Results.Created($"/api/v1/accessories/items/{id}", new { id, name = body.Name.Trim() });
        }).RequireAuthorization(Permissions.AccessoriesManage);

        g.MapPut("/items/{id:guid}", async (Guid id, ItemBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (Check(body) is { } problem) return problem;
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var hist = new NpgsqlCommand("SELECT serial_tracked, (SELECT count(*) FROM zentriq.accessory_movements WHERE item_id = @id) FROM zentriq.accessory_items WHERE id = @id", conn, tx))
            {
                hist.Parameters.AddWithValue("id", id);
                await using var r = await hist.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                if (r.GetBoolean(0) != (body.SerialTracked == true) && r.GetInt64(1) > 0)
                    return Validation.Problem("serialTracked", "Serial tracking cannot be switched once the accessory has stock movements.");
            }
            await using var cmd = new NpgsqlCommand("""
                UPDATE zentriq.accessory_items SET name = @n, category = @c, brand = @b, model = @m, sku = @sku, unit_cost = @uc, reorder_level = @rl,
                       returnable = @ret, serial_tracked = @ser, notes = @notes, active = @act, updated_at = now()
                 WHERE id = @id
                """, conn, tx);
            Fill(cmd, id, body);
            cmd.Parameters.AddWithValue("act", body.Active != false);
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "Another accessory has this name or SKU."); }
            await AccessoryStore.RefreshLowAsync(conn, tx, id, ct);
            await tx.CommitAsync(ct);
            await EnsureCategoryAsync(ds, body.Category!, ct);
            await rc.AuditAsync("accessory.item_update", body.Name.Trim(), detail: new { id });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AccessoriesManage);

        g.MapDelete("/items/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                DELETE FROM zentriq.accessory_items i WHERE i.id = @id AND NOT EXISTS (SELECT 1 FROM zentriq.accessory_movements m WHERE m.item_id = i.id) RETURNING name
                """);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is string name) { await rc.AuditAsync("accessory.item_delete", name); return Results.NoContent(); }
            await using var ex = ds.CreateCommand("SELECT 1 FROM zentriq.accessory_items WHERE id = @id");
            ex.Parameters.AddWithValue("id", id);
            return await ex.ExecuteScalarAsync(ct) is null ? Results.NotFound()
                : Results.Problem(statusCode: 409, title: "This accessory has stock history and cannot be deleted. Edit it and untick Active to hide it; the history is kept.");
        }).RequireAuthorization(Permissions.AccessoriesManage);

        // ---- stock -------------------------------------------------------------------------------------------
        g.MapGet("/stock", async (NpgsqlDataSource ds, string? q, Guid? locationId, string? category, bool? low, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT i.id, i.name, i.category, i.brand, i.model, i.sku, i.reorder_level, i.returnable, i.serial_tracked, i.unit_cost,
                       lo.id, lo.name, COALESCE(s.quantity, 0), COALESCE(s.faulty, 0), s.updated_at, s.low_since
                  FROM zentriq.accessory_items i
                  CROSS JOIN zentriq.locations lo
                  LEFT JOIN zentriq.accessory_stock s ON s.item_id = i.id AND s.location_id = lo.id
                 WHERE i.active AND (s.item_id IS NOT NULL OR @loc::uuid IS NOT NULL OR (SELECT count(*) FROM zentriq.locations) = 1)
                   AND (@loc::uuid IS NULL OR lo.id = @loc) AND (@cat::text IS NULL OR i.category = @cat)
                   AND (@q::text IS NULL OR i.name ILIKE @q OR i.brand ILIKE @q OR i.model ILIKE @q OR i.sku ILIKE @q)
                   AND (NOT @low OR (i.reorder_level > 0 AND COALESCE(s.quantity, 0) <= i.reorder_level))
                 ORDER BY lower(lo.name), i.category, lower(i.name)
                """);
            cmd.Parameters.Add(new NpgsqlParameter("loc", NpgsqlDbType.Uuid) { Value = (object?)locationId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("cat", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(category) ? DBNull.Value : category });
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : "%" + q.Trim() + "%" });
            cmd.Parameters.AddWithValue("low", low == true);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var qty = r.GetInt32(12); var reorder = r.GetInt32(6);
                list.Add(new
                {
                    id = $"{r.GetGuid(0):N}:{r.GetGuid(10):N}", itemId = r.GetGuid(0), item = r.GetString(1), category = r.GetString(2), brand = S(3), model = S(4), sku = S(5),
                    reorderLevel = reorder, returnable = r.GetBoolean(7), serialTracked = r.GetBoolean(8), unitCost = r.IsDBNull(9) ? (decimal?)null : r.GetDecimal(9),
                    locationId = r.GetGuid(10), location = r.GetString(11), quantity = qty, faulty = r.GetInt32(13),
                    updatedAt = r.IsDBNull(14) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(14), low = reorder > 0 && qty <= reorder,
                    lowSince = r.IsDBNull(15) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(15),
                });
            }
            return Results.Ok(list);
        });

        // ---- movements ---------------------------------------------------------------------------------------
        g.MapPost("/movements", async (MovementBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            List<long> ids;
            try
            {
                ids = await AccessoryStore.ApplyAsync(conn, tx, new Movement(body.Kind, body.LocationId,
                    (body.Lines ?? []).Select(l => new MovementLine(l.ItemId, l.Quantity, l.UnitCost, l.Serials)).ToList(),
                    body.ToLocationId, body.Person, body.DepartmentId, body.AssetId, body.Condition, body.Reason, body.Reference, body.Vendor, body.Note,
                    rc.UserId, rc.UserName), ct);
            }
            catch (AccessoryValidationException ex) { return Validation.Problem(ex.Field, ex.Message); }
            await tx.CommitAsync(ct);
            var count = body.Lines?.Sum(l => l.Quantity) ?? 0;
            await rc.AuditAsync($"accessory.{body.Kind}", body.Person ?? body.Kind, detail: new { body.LocationId, body.ToLocationId, lines = body.Lines?.Length, count, body.Reference });
            var what = body.Kind switch
            {
                "receive" => $"{count} accessor{(count == 1 ? "y" : "ies")} received.",
                "issue" => $"{count} accessor{(count == 1 ? "y" : "ies")} issued{(string.IsNullOrWhiteSpace(body.Person) ? "" : " to " + body.Person!.Trim())}.",
                "return" => $"{count} accessor{(count == 1 ? "y" : "ies")} returned{(body.Condition == "faulty" ? " as faulty" : " to stock")}.",
                "transfer" => $"{count} accessor{(count == 1 ? "y" : "ies")} transferred.",
                "writeoff" => $"{count} accessor{(count == 1 ? "y" : "ies")} written off.",
                _ => "Stock count recorded.",
            };
            return Results.Ok(new { ids, message = what });
        }).RequireAuthorization(Permissions.AccessoriesManage);

        g.MapGet("/movements", async (NpgsqlDataSource ds, string? kind, Guid? itemId, Guid? locationId, Guid? departmentId, string? person, int? days, string? q, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, size) = Paging.Normalize(page, pageSize);
            await using var cmd = ds.CreateCommand("""
                SELECT m.id, m.occurred_at, m.kind, i.id, i.name, i.category, m.quantity, m.faulty_change, lo.name, lt.name, m.person, dp.name,
                       m.asset_id, COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)), m.condition, m.reason, m.reference, m.vendor, m.unit_cost, m.serials, m.note, m.by_name,
                       count(*) OVER (), m.department_id, m.location_id
                  FROM zentriq.accessory_movements m
                  JOIN zentriq.accessory_items i ON i.id = m.item_id
                  JOIN zentriq.locations lo ON lo.id = m.location_id
                  LEFT JOIN zentriq.locations lt ON lt.id = m.to_location_id
                  LEFT JOIN zentriq.departments dp ON dp.id = m.department_id
                  LEFT JOIN zentriq.assets a ON a.id = m.asset_id
                 WHERE (@k::text IS NULL OR m.kind = @k) AND (@i::uuid IS NULL OR m.item_id = @i)
                   AND (@l::uuid IS NULL OR m.location_id = @l OR m.to_location_id = @l) AND (@d::uuid IS NULL OR m.department_id = @d)
                   AND (@p::text IS NULL OR m.person_key = @p) AND m.occurred_at > now() - make_interval(days => @days)
                   AND (@q::text IS NULL OR i.name ILIKE @q OR m.person ILIKE @q OR m.reference ILIKE @q OR m.vendor ILIKE @q OR array_to_string(m.serials, ' ') ILIKE @q)
                 ORDER BY m.id DESC LIMIT @lim OFFSET @off
                """);
            cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = AccessoryStore.Kinds.Contains(kind) ? kind : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Uuid) { Value = (object?)itemId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = (object?)locationId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Uuid) { Value = (object?)departmentId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = (object?)AccessoryStore.PersonKey(person) ?? DBNull.Value });
            cmd.Parameters.AddWithValue("days", Math.Clamp(days ?? 90, 1, 3660));
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : "%" + q.Trim() + "%" });
            cmd.Parameters.AddWithValue("lim", size);
            cmd.Parameters.AddWithValue("off", (p - 1) * size);
            var items = new List<object>();
            long total = 0;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                items.Add(new
                {
                    id = r.GetInt64(0), occurredAt = r.GetFieldValue<DateTimeOffset>(1), kind = r.GetString(2), itemId = r.GetGuid(3), item = r.GetString(4), category = r.GetString(5),
                    quantity = r.GetInt32(6), faultyChange = r.GetInt32(7), location = r.GetString(8), toLocation = S(9), person = S(10), department = S(11),
                    assetId = r.IsDBNull(12) ? (Guid?)null : r.GetGuid(12), device = S(13), condition = S(14), reason = S(15), reference = S(16), vendor = S(17),
                    unitCost = r.IsDBNull(18) ? (decimal?)null : r.GetDecimal(18), serials = r.GetFieldValue<string[]>(19), note = S(20), by = S(21),
                    departmentId = r.IsDBNull(23) ? (Guid?)null : r.GetGuid(23), locationId = r.GetGuid(24),
                });
                total = r.GetInt64(22);
            }
            return Results.Ok(new { items, total, page = p, pageSize = size });
        });

        // Who holds what: issued minus returned, per person and accessory (returnable first).
        g.MapGet("/holdings", async (NpgsqlDataSource ds, string? q, Guid? departmentId, bool? returnableOnly, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT x.person_key, x.person, i.id, i.name, i.category, i.returnable, x.held, x.last_issued, dp.id, dp.name, x.serials
                  FROM (SELECT m.person_key, m.item_id,
                               (array_agg(m.person ORDER BY m.id DESC))[1] AS person,
                               sum(CASE m.kind WHEN 'issue' THEN -m.quantity ELSE -(m.quantity + m.faulty_change) END) AS held,
                               max(m.occurred_at) FILTER (WHERE m.kind = 'issue') AS last_issued,
                               (array_agg(m.department_id ORDER BY m.id DESC) FILTER (WHERE m.kind = 'issue'))[1] AS department_id,
                               (SELECT array_agg(u.serial ORDER BY u.serial) FROM zentriq.accessory_units u WHERE u.item_id = m.item_id AND u.person_key = m.person_key AND u.status = 'issued') AS serials
                          FROM zentriq.accessory_movements m
                         WHERE m.kind IN ('issue','return') AND m.person_key IS NOT NULL
                         GROUP BY m.person_key, m.item_id) x
                  JOIN zentriq.accessory_items i ON i.id = x.item_id
                  LEFT JOIN zentriq.departments dp ON dp.id = x.department_id
                 WHERE x.held > 0 AND (@d::uuid IS NULL OR x.department_id = @d) AND (NOT @ret OR i.returnable)
                   AND (@q::text IS NULL OR x.person ILIKE @q OR i.name ILIKE @q)
                 ORDER BY lower(x.person), i.name
                """);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Uuid) { Value = (object?)departmentId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("ret", returnableOnly == true);
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : "%" + q.Trim() + "%" });
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = $"{r.GetString(0)}:{r.GetGuid(2):N}", personKey = r.GetString(0), person = r.GetString(1), itemId = r.GetGuid(2), item = r.GetString(3), category = r.GetString(4),
                    returnable = r.GetBoolean(5), quantity = Convert.ToInt32(r.GetValue(6)), lastIssued = r.IsDBNull(7) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(7),
                    departmentId = r.IsDBNull(8) ? (Guid?)null : r.GetGuid(8), department = r.IsDBNull(9) ? null : r.GetString(9),
                    serials = r.IsDBNull(10) ? Array.Empty<string>() : r.GetFieldValue<string[]>(10),
                });
            return Results.Ok(list);
        });

        // Serial-numbered units of an item (to pick when issuing, returning, transferring or writing off).
        g.MapGet("/items/{id:guid}/units", async (Guid id, NpgsqlDataSource ds, string? status, Guid? locationId, string? person, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT u.serial, u.status, u.location_id, lo.name, u.person FROM zentriq.accessory_units u LEFT JOIN zentriq.locations lo ON lo.id = u.location_id
                 WHERE u.item_id = @i AND (@s::text IS NULL OR u.status = @s) AND (@l::uuid IS NULL OR u.location_id = @l) AND (@p::text IS NULL OR u.person_key = @p)
                 ORDER BY u.serial LIMIT 2000
                """);
            cmd.Parameters.AddWithValue("i", id);
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = status is "in_stock" or "issued" or "faulty" or "written_off" ? status : DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = (object?)locationId ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = (object?)AccessoryStore.PersonKey(person) ?? DBNull.Value });
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new { serial = r.GetString(0), status = r.GetString(1), locationId = r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2), location = r.IsDBNull(3) ? null : r.GetString(3), person = r.IsDBNull(4) ? null : r.GetString(4) });
            return Results.Ok(list);
        });

        // Names to choose from when issuing: people accessories were given to, directory user accounts, logged-on users.
        g.MapGet("/people", async (NpgsqlDataSource ds, string? q, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT name, source FROM (
                    (SELECT DISTINCT ON (person_key) person AS name, 'issued' AS source, 1 AS o FROM zentriq.accessory_movements
                      WHERE person IS NOT NULL AND (@q::text IS NULL OR person ILIKE @q) ORDER BY person_key, id DESC)
                    UNION ALL
                    SELECT DISTINCT COALESCE(substring(distinguished_name FROM '^CN=([^,]+)'), sam_account_name), 'directory', 2 FROM zentriq.directory_objects
                     WHERE object_class = 'user' AND enabled IS NOT FALSE AND (@q::text IS NULL OR distinguished_name ILIKE @q OR sam_account_name ILIKE @q)
                    UNION ALL
                    SELECT DISTINCT d.document #>> '{users,loggedOn}', 'device', 3 FROM zentriq.asset_inventory_documents d
                     WHERE d.document #>> '{users,loggedOn}' IS NOT NULL AND (@q::text IS NULL OR d.document #>> '{users,loggedOn}' ILIKE @q)
                ) x WHERE name IS NOT NULL ORDER BY o, lower(name) LIMIT 50
                """);
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : "%" + q.Trim() + "%" });
            var list = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (seen.Add(r.GetString(0))) list.Add(new { name = r.GetString(0), source = r.GetString(1) });
            return Results.Ok(list);
        });
    }

    public sealed record CategoryBody(string? Name);

    private static async Task<string[]> CategoryNamesAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT name FROM zentriq.accessory_categories ORDER BY lower(name)");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list.ToArray();
    }

    private static async Task EnsureCategoryAsync(NpgsqlDataSource ds, string category, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("INSERT INTO zentriq.accessory_categories (name) VALUES (@n) ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue("n", category.Trim());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static IResult? Check(ItemBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 150) return Validation.Problem("name", "Enter the accessory name (up to 150 characters).");
        if (string.IsNullOrWhiteSpace(b.Category) || b.Category.Trim().Length > 60) return Validation.Problem("category", "Choose or enter a category.");
        if (b.UnitCost is < 0) return Validation.Problem("unitCost", "The unit cost cannot be negative.");
        if (b.ReorderLevel is < 0 or > 1_000_000) return Validation.Problem("reorderLevel", "The reorder level must be 0 or more.");
        if (b.Sku is { Length: > 100 } || b.Brand is { Length: > 100 } || b.Model is { Length: > 100 }) return Validation.Problem("sku", "Brand, model and SKU can have at most 100 characters.");
        return null;
    }

    private static void Fill(NpgsqlCommand cmd, Guid id, ItemBody b)
    {
        static object T(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s.Trim();
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("n", b.Name.Trim());
        cmd.Parameters.AddWithValue("c", b.Category.Trim());
        cmd.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Text) { Value = T(b.Brand) });
        cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Text) { Value = T(b.Model) });
        cmd.Parameters.Add(new NpgsqlParameter("sku", NpgsqlDbType.Text) { Value = T(b.Sku) });
        cmd.Parameters.Add(new NpgsqlParameter("uc", NpgsqlDbType.Numeric) { Value = (object?)b.UnitCost ?? DBNull.Value });
        cmd.Parameters.AddWithValue("rl", b.ReorderLevel ?? 0);
        cmd.Parameters.AddWithValue("ret", b.Returnable != false);
        cmd.Parameters.AddWithValue("ser", b.SerialTracked == true);
        cmd.Parameters.Add(new NpgsqlParameter("notes", NpgsqlDbType.Text) { Value = T(b.Notes) });
    }
}
