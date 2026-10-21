using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Accessories;

/// <summary>One line of a stock movement: an accessory and a quantity (and the serial numbers of serial-tracked items).</summary>
public sealed record MovementLine(Guid ItemId, int Quantity, decimal? UnitCost = null, string[]? Serials = null);

/// <summary>
/// A stock movement. Kinds: receive (+), issue (− to a person), return (+ usable or faulty), transfer (− here, + there),
/// writeoff (− usable or faulty), adjust (stock count: the counted quantity replaces the quantity on hand).
/// </summary>
public sealed record Movement(
    string Kind, Guid LocationId, IReadOnlyList<MovementLine> Lines,
    Guid? ToLocationId = null, string? Person = null, Guid? DepartmentId = null, Guid? AssetId = null,
    string? Condition = null, string? Reason = null, string? Reference = null, string? Vendor = null, string? Note = null,
    Guid? ByUser = null, string? ByName = null);

public sealed class AccessoryValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// IT accessories stock. Stock is never edited directly: every change is a movement recorded in
/// zentriq.accessory_movements and applied to zentriq.accessory_stock (and the serial-numbered units) in the same
/// transaction, with the stock row locked, so concurrent issues cannot take stock below zero.
/// </summary>
public static class AccessoryStore
{
    public static readonly string[] Kinds = ["receive", "issue", "return", "transfer", "writeoff", "adjust"];

    public static readonly string[] DefaultCategories =
        ["Mouse", "Keyboard", "Keyboard and mouse set", "Headset", "Webcam", "Docking station", "Monitor cable", "Adapter / dongle",
         "Charger / power adapter", "USB drive", "Laptop bag", "Mouse pad", "Speaker", "Battery", "Other"];

    public static string? PersonKey(string? person) => string.IsNullOrWhiteSpace(person) ? null : string.Join(' ', person.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private sealed record ItemInfo(string Name, bool SerialTracked, bool Returnable);

    /// <summary>Applies a movement (all lines or none). Returns the ids of the movement rows written.</summary>
    public static async Task<List<long>> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Movement m, CancellationToken ct)
    {
        if (!Kinds.Contains(m.Kind)) throw new AccessoryValidationException("kind", "Unknown movement.");
        if (m.Lines is not { Count: > 0 } || m.Lines.Count > 200) throw new AccessoryValidationException("lines", "Add 1–200 accessories.");
        if (m.Lines.Select(l => l.ItemId).Distinct().Count() != m.Lines.Count) throw new AccessoryValidationException("lines", "Each accessory can appear only once.");
        var person = string.IsNullOrWhiteSpace(m.Person) ? null : m.Person.Trim();
        if (person is { Length: > 200 }) throw new AccessoryValidationException("person", "The name can have at most 200 characters.");
        if (m.Kind is "issue" && person is null && m.DepartmentId is null) throw new AccessoryValidationException("person", "Choose who receives the accessories (a person or at least a department).");
        if (m.Kind is "return" && person is null) throw new AccessoryValidationException("person", "Choose who returns the accessories.");
        if (m.Kind is "return" or "writeoff" && m.Condition is not ("good" or "faulty")) throw new AccessoryValidationException("condition", "Choose the condition (good or faulty).");
        if (m.Kind is "writeoff" && string.IsNullOrWhiteSpace(m.Reason)) throw new AccessoryValidationException("reason", "Give the reason (damaged, lost, scrapped…).");
        if (m.Kind is "transfer" && (m.ToLocationId is null || m.ToLocationId == m.LocationId)) throw new AccessoryValidationException("toLocationId", "Choose another location to transfer to.");
        if (m.Kind is "receive" && m.Lines.Any(l => l.UnitCost is < 0)) throw new AccessoryValidationException("unitCost", "The unit cost cannot be negative.");
        foreach (var l in m.Lines)
            if (m.Kind == "adjust" ? l.Quantity is < 0 or > 1_000_000 : l.Quantity is < 1 or > 1_000_000)
                throw new AccessoryValidationException("quantity", m.Kind == "adjust" ? "Enter the counted quantity (0 or more)." : "Enter a quantity of at least 1.");

        await EnsureLocationAsync(conn, tx, m.LocationId, "locationId", ct);
        if (m.ToLocationId is { } to) await EnsureLocationAsync(conn, tx, to, "toLocationId", ct);
        if (m.DepartmentId is { } dep && await ScalarAsync(conn, tx, "SELECT 1 FROM zentriq.departments WHERE id = @id", ct, ("id", dep)) is null)
            throw new AccessoryValidationException("departmentId", "The department no longer exists.");
        if (m.AssetId is { } asset && await ScalarAsync(conn, tx, "SELECT 1 FROM zentriq.assets WHERE id = @id", ct, ("id", asset)) is null)
            throw new AccessoryValidationException("assetId", "The device no longer exists.");

        var ids = new List<long>();
        // Lock the stock rows in a fixed order (item, location) so concurrent movements cannot deadlock.
        foreach (var line in m.Lines.OrderBy(l => l.ItemId))
        {
            var item = await ItemAsync(conn, tx, line.ItemId, ct);
            var serials = (line.Serials ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (item.SerialTracked && m.Kind != "adjust" && serials.Length != line.Quantity)
                throw new AccessoryValidationException("serials", $"{item.Name} is tracked by serial number: enter {line.Quantity} serial number(s) ({serials.Length} given).");
            if (item.SerialTracked && m.Kind == "adjust")
                throw new AccessoryValidationException("lines", $"{item.Name} is tracked by serial number: correct its units with receive, return or write-off instead of a stock count.");
            if (!item.SerialTracked) serials = [];

            var (qty, faulty) = await LockStockAsync(conn, tx, line.ItemId, m.LocationId, ct);
            int dQty = 0, dFaulty = 0;
            switch (m.Kind)
            {
                case "receive":
                    dQty = line.Quantity;
                    await ReceiveUnitsAsync(conn, tx, line.ItemId, m.LocationId, serials, item.Name, ct);
                    if (line.UnitCost is { } cost)
                        await ExecAsync(conn, tx, "UPDATE zentriq.accessory_items SET unit_cost = @c, updated_at = now() WHERE id = @id", ct, ("c", cost), ("id", line.ItemId));
                    break;
                case "issue":
                    if (qty < line.Quantity) throw Short(item.Name, qty, line.Quantity);
                    dQty = -line.Quantity;
                    await MoveUnitsAsync(conn, tx, line.ItemId, serials, "in_stock", m.LocationId, "issued", null, person, m.DepartmentId, m.AssetId, item.Name, ct);
                    break;
                case "return":
                    var held = await HeldAsync(conn, tx, line.ItemId, PersonKey(person)!, ct);
                    if (held < line.Quantity)
                        throw new AccessoryValidationException("quantity", held == 0 ? $"{person} has no {item.Name} to return." : $"{person} holds only {held} × {item.Name}.");
                    if (m.Condition == "faulty") dFaulty = line.Quantity; else dQty = line.Quantity;
                    await MoveUnitsAsync(conn, tx, line.ItemId, serials, "issued", null, m.Condition == "faulty" ? "faulty" : "in_stock", m.LocationId, null, null, null, item.Name, ct);
                    break;
                case "transfer":
                    if (qty < line.Quantity) throw Short(item.Name, qty, line.Quantity);
                    dQty = -line.Quantity;
                    break;
                case "writeoff":
                    if (m.Condition == "faulty")
                    {
                        if (faulty < line.Quantity) throw new AccessoryValidationException("quantity", $"Only {faulty} faulty × {item.Name} at this location.");
                        dFaulty = -line.Quantity;
                    }
                    else
                    {
                        if (qty < line.Quantity) throw Short(item.Name, qty, line.Quantity);
                        dQty = -line.Quantity;
                    }
                    await MoveUnitsAsync(conn, tx, line.ItemId, serials, m.Condition == "faulty" ? "faulty" : "in_stock", m.LocationId, "written_off", null, null, null, null, item.Name, ct);
                    break;
                case "adjust":
                    dQty = line.Quantity - qty;
                    if (dQty == 0) throw new AccessoryValidationException("quantity", $"The count of {item.Name} matches the stock ({qty}); nothing to correct.");
                    break;
            }
            await ChangeStockAsync(conn, tx, line.ItemId, m.LocationId, dQty, dFaulty, ct);
            if (m.Kind == "transfer")
            {
                await LockStockAsync(conn, tx, line.ItemId, m.ToLocationId!.Value, ct);
                await ChangeStockAsync(conn, tx, line.ItemId, m.ToLocationId.Value, line.Quantity, 0, ct);
                await MoveUnitsAsync(conn, tx, line.ItemId, serials, "in_stock", m.LocationId, "in_stock", m.ToLocationId, null, null, null, item.Name, ct);
            }

            await using var ins = new NpgsqlCommand("""
                INSERT INTO zentriq.accessory_movements (kind, item_id, location_id, to_location_id, quantity, faulty_change, person, person_key,
                    department_id, asset_id, condition, reason, reference, vendor, unit_cost, serials, note, by_user, by_name)
                VALUES (@k, @i, @l, @to, @q, @f, @p, @pk, @dep, @a, @c, @r, @ref, @v, @uc, @s, @n, @bu, @bn)
                RETURNING id
                """, conn, tx);
            ins.Parameters.AddWithValue("k", m.Kind);
            ins.Parameters.AddWithValue("i", line.ItemId);
            ins.Parameters.AddWithValue("l", m.LocationId);
            ins.Parameters.Add(Uuid("to", m.Kind == "transfer" ? m.ToLocationId : null));
            ins.Parameters.AddWithValue("q", dQty);
            ins.Parameters.AddWithValue("f", dFaulty);
            ins.Parameters.Add(Text("p", m.Kind is "issue" or "return" ? person : null));
            ins.Parameters.Add(Text("pk", m.Kind is "issue" or "return" ? PersonKey(person) : null));
            ins.Parameters.Add(Uuid("dep", m.DepartmentId));
            ins.Parameters.Add(Uuid("a", m.AssetId));
            ins.Parameters.Add(Text("c", m.Kind is "return" or "writeoff" ? m.Condition : null));
            ins.Parameters.Add(Text("r", Trim(m.Reason, 200) ?? (m.Kind == "adjust" ? "Stock count" : null)));
            ins.Parameters.Add(Text("ref", Trim(m.Reference, 100)));
            ins.Parameters.Add(Text("v", Trim(m.Vendor, 200)));
            ins.Parameters.Add(new NpgsqlParameter("uc", NpgsqlDbType.Numeric) { Value = (object?)line.UnitCost ?? DBNull.Value });
            ins.Parameters.AddWithValue("s", serials);
            ins.Parameters.Add(Text("n", Trim(m.Note, 1000)));
            ins.Parameters.Add(Uuid("bu", m.ByUser));
            ins.Parameters.Add(Text("bn", m.ByName));
            ids.Add((long)(await ins.ExecuteScalarAsync(ct))!);
        }
        return ids;
    }

    /// <summary>How many of an item a person still holds (issued minus returned).</summary>
    public static async Task<int> HeldAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid itemId, string personKey, CancellationToken ct) =>
        Convert.ToInt32(await ScalarAsync(conn, tx, """
            SELECT COALESCE(sum(CASE kind WHEN 'issue' THEN -quantity WHEN 'return' THEN -(quantity + faulty_change) ELSE 0 END), 0)
              FROM zentriq.accessory_movements WHERE item_id = @i AND person_key = @p AND kind IN ('issue','return')
            """, ct, ("i", itemId), ("p", personKey)) ?? 0);

    private static AccessoryValidationException Short(string item, int have, int want) =>
        new("quantity", have == 0 ? $"{item} is out of stock at this location." : $"Only {have} × {item} in stock at this location ({want} requested).");

    private static async Task EnsureLocationAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string field, CancellationToken ct)
    {
        if (await ScalarAsync(conn, tx, "SELECT 1 FROM zentriq.locations WHERE id = @id", ct, ("id", id)) is null)
            throw new AccessoryValidationException(field, "The location no longer exists.");
    }

    private static async Task<ItemInfo> ItemAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT name, serial_tracked, returnable, active FROM zentriq.accessory_items WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new AccessoryValidationException("lines", "An accessory in the list no longer exists.");
        return new ItemInfo(r.GetString(0), r.GetBoolean(1), r.GetBoolean(2));
    }

    private static async Task<(int Quantity, int Faulty)> LockStockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid item, Guid location, CancellationToken ct)
    {
        await ExecAsync(conn, tx, "INSERT INTO zentriq.accessory_stock (item_id, location_id) VALUES (@i, @l) ON CONFLICT DO NOTHING", ct, ("i", item), ("l", location));
        await using var cmd = new NpgsqlCommand("SELECT quantity, faulty FROM zentriq.accessory_stock WHERE item_id = @i AND location_id = @l FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("i", item);
        cmd.Parameters.AddWithValue("l", location);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetInt32(0), r.GetInt32(1));
    }

    /// <summary>Changes the stock and keeps low_since (when it fell to or below the reorder level; null when above).</summary>
    private static Task ChangeStockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid item, Guid location, int dQty, int dFaulty, CancellationToken ct) =>
        ExecAsync(conn, tx, """
            UPDATE zentriq.accessory_stock s
               SET quantity = s.quantity + @q, faulty = s.faulty + @f, updated_at = now(),
                   low_since = CASE WHEN i.reorder_level > 0 AND s.quantity + @q <= i.reorder_level THEN COALESCE(s.low_since, now()) END
              FROM zentriq.accessory_items i
             WHERE i.id = s.item_id AND s.item_id = @i AND s.location_id = @l
            """, ct, ("q", dQty), ("f", dFaulty), ("i", item), ("l", location));

    /// <summary>Re-evaluates low_since for every location of an item (its reorder level changed).</summary>
    public static Task RefreshLowAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid item, CancellationToken ct) =>
        ExecAsync(conn, tx, """
            UPDATE zentriq.accessory_stock s
               SET low_since = CASE WHEN i.reorder_level > 0 AND s.quantity <= i.reorder_level THEN COALESCE(s.low_since, now()) END
              FROM zentriq.accessory_items i WHERE i.id = s.item_id AND s.item_id = @i
            """, ct, ("i", item));

    private static async Task ReceiveUnitsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid item, Guid location, string[] serials, string name, CancellationToken ct)
    {
        foreach (var s in serials)
        {
            if (s.Length > 100) throw new AccessoryValidationException("serials", "A serial number can have at most 100 characters.");
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.accessory_units (item_id, serial, location_id, status) VALUES (@i, @s, @l, 'in_stock')
                ON CONFLICT (item_id, serial) DO UPDATE SET location_id = EXCLUDED.location_id, status = 'in_stock', person = NULL, person_key = NULL,
                    department_id = NULL, asset_id = NULL, updated_at = now()
                 WHERE zentriq.accessory_units.status = 'written_off'
                """, conn, tx);
            cmd.Parameters.AddWithValue("i", item);
            cmd.Parameters.AddWithValue("s", s);
            cmd.Parameters.AddWithValue("l", location);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new AccessoryValidationException("serials", $"{name} with serial number {s} is already registered.");
        }
    }

    private static async Task MoveUnitsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid item, string[] serials, string fromStatus, Guid? fromLocation,
        string toStatus, Guid? toLocation, string? person, Guid? department, Guid? asset, string name, CancellationToken ct)
    {
        foreach (var s in serials)
        {
            await using var cmd = new NpgsqlCommand("""
                UPDATE zentriq.accessory_units
                   SET status = @to, location_id = COALESCE(@tl, location_id), person = @p, person_key = @pk, department_id = @dep, asset_id = @a, updated_at = now()
                 WHERE item_id = @i AND lower(serial) = lower(@s) AND status = @from AND (@fl::uuid IS NULL OR location_id = @fl)
                """, conn, tx);
            cmd.Parameters.AddWithValue("to", toStatus);
            cmd.Parameters.Add(Uuid("tl", toLocation));
            cmd.Parameters.Add(Text("p", toStatus == "issued" ? person : null));
            cmd.Parameters.Add(Text("pk", toStatus == "issued" ? PersonKey(person) : null));
            cmd.Parameters.Add(Uuid("dep", toStatus == "issued" ? department : null));
            cmd.Parameters.Add(Uuid("a", toStatus == "issued" ? asset : null));
            cmd.Parameters.AddWithValue("i", item);
            cmd.Parameters.AddWithValue("s", s);
            cmd.Parameters.AddWithValue("from", fromStatus);
            cmd.Parameters.Add(Uuid("fl", fromLocation));
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                throw new AccessoryValidationException("serials", fromStatus switch
                {
                    "issued" => $"{name} {s} is not issued to anyone.",
                    "faulty" => $"{name} {s} is not among the faulty units at this location.",
                    _ => $"{name} {s} is not in stock at this location.",
                });
        }
    }

    private static NpgsqlParameter Uuid(string n, Guid? v) => new(n, NpgsqlDbType.Uuid) { Value = (object?)v ?? DBNull.Value };
    private static NpgsqlParameter Text(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = (object?)v ?? DBNull.Value };
    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();

    private static async Task<object?> ScalarAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        var r = await cmd.ExecuteScalarAsync(ct);
        return r is DBNull ? null : r;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
