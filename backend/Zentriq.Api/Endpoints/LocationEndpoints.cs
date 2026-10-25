using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Locations (offices, sites, floors, stores) that IT assets are assigned to, and where IT accessories are stocked.
/// Used like departments: a column and a filter in the inventories, a "Location" column in every device report.
/// </summary>
public static class LocationEndpoints
{
    public sealed record LocationBody(string Name, string? Code, string? Description);
    public sealed record AssignBody(Guid[] AssetIds, Guid? LocationId);

    public static void MapLocationEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/locations").WithTags("Locations").RequireAuthorization(Permissions.AnyAssetView);

        g.MapGet("", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var list = new List<object>();
            await using var cmd = ds.CreateCommand("""
                SELECT l.id, l.name, l.code, l.description, l.created_at,
                       (SELECT count(*) FROM zentriq.assets a WHERE a.location_id = l.id AND a.status = 'active'),
                       (SELECT COALESCE(sum(s.quantity), 0) FROM zentriq.accessory_stock s WHERE s.location_id = l.id)
                  FROM zentriq.locations l ORDER BY lower(l.name)
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), code = r.IsDBNull(2) ? null : r.GetString(2), description = r.IsDBNull(3) ? null : r.GetString(3),
                    createdAt = r.GetFieldValue<DateTimeOffset>(4), assets = r.GetInt64(5), accessories = Convert.ToInt64(r.GetValue(6)),
                });
            await using var un = ds.CreateCommand("SELECT count(*) FROM zentriq.assets WHERE status = 'active' AND location_id IS NULL");
            return Results.Ok(new { items = list, unassigned = (long)(await un.ExecuteScalarAsync(ct))! });
        });

        g.MapPost("", async (LocationBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (Check(body) is { } problem) return problem;
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand("INSERT INTO zentriq.locations (id, name, code, description, created_by) VALUES (@id, @n, @c, @d, @u)");
            Fill(cmd, id, body);
            cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A location with this name already exists."); }
            await rc.AuditAsync("location.create", body.Name.Trim());
            return Results.Created($"/api/v1/locations/{id}", new { id, name = body.Name.Trim() });
        }).RequireAuthorization(Permissions.AnyAssetManage);

        g.MapPut("/{id:guid}", async (Guid id, LocationBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (Check(body) is { } problem) return problem;
            await using var cmd = ds.CreateCommand("UPDATE zentriq.locations SET name = @n, code = @c, description = @d WHERE id = @id");
            Fill(cmd, id, body);
            try { if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A location with this name already exists."); }
            await rc.AuditAsync("location.update", body.Name.Trim(), detail: new { id });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AnyAssetManage);

        g.MapDelete("/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            // A location that holds accessories or has stock history cannot be deleted (the history refers to it).
            await using (var used = ds.CreateCommand("""
                SELECT EXISTS (SELECT 1 FROM zentriq.accessory_movements WHERE location_id = @id OR to_location_id = @id)
                    OR EXISTS (SELECT 1 FROM zentriq.accessory_stock WHERE location_id = @id AND (quantity > 0 OR faulty > 0))
                """))
            {
                used.Parameters.AddWithValue("id", id);
                if (await used.ExecuteScalarAsync(ct) is true)
                    return Results.Problem(statusCode: 409, title: "This location has IT accessories stock or stock history. Transfer the stock to another location instead; the history is kept.");
            }
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.locations WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("location.delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AnyAssetManage);

        // Assign (or clear, with locationId = null) the location of one or more assets.
        api.MapPost("/assets/location", async (AssignBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (body.AssetIds is not { Length: > 0 } || body.AssetIds.Length > 10_000) return Validation.Problem("assetIds", "Select 1–10000 assets.");
            string? name = null;
            if (body.LocationId is { } loc)
            {
                await using var chk = ds.CreateCommand("SELECT name FROM zentriq.locations WHERE id = @id");
                chk.Parameters.AddWithValue("id", loc);
                name = await chk.ExecuteScalarAsync(ct) as string;
                if (name is null) return Validation.Problem("locationId", "The location no longer exists.");
            }
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.assets SET location_id = @l, updated_at = now(), row_version = row_version + 1
                 WHERE id = ANY(@ids) AND location_id IS DISTINCT FROM @l
                """);
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = (object?)body.LocationId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("ids", body.AssetIds.Distinct().ToArray());
            var n = await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("asset.location", name ?? "(none)", detail: new { assets = body.AssetIds.Length, changed = n, body.LocationId });
            return Results.Ok(new { updated = n, location = name });
        }).WithTags("Assets").RequireModule(Modules.Inventory).RequireAuthorization(Permissions.AssetsManage);
    }

    private static IResult? Check(LocationBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 100) return Validation.Problem("name", "Enter a location name (up to 100 characters).");
        if (b.Code is { Length: > 30 }) return Validation.Problem("code", "The code can have at most 30 characters.");
        if (b.Description is { Length: > 500 }) return Validation.Problem("description", "The description can have at most 500 characters.");
        return null;
    }

    private static void Fill(NpgsqlCommand cmd, Guid id, LocationBody b)
    {
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("n", b.Name.Trim());
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(b.Code) ? DBNull.Value : b.Code.Trim() });
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(b.Description) ? DBNull.Value : b.Description.Trim() });
    }
}
