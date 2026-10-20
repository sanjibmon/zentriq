using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Departments / units that assets are assigned to after discovery. Used to filter the asset list, in reports
/// ("Department" column on every device-based data source) and on the dashboard.
/// </summary>
public static class DepartmentEndpoints
{
    public sealed record DepartmentBody(string Name, string? Code, string? Description);
    public sealed record AssignBody(Guid[] AssetIds, Guid? DepartmentId);

    public static void MapDepartmentEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/departments").WithTags("Departments").RequireAuthorization(Permissions.AssetsView);

        g.MapGet("", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var list = new List<object>();
            await using var cmd = ds.CreateCommand("""
                SELECT d.id, d.name, d.code, d.description, d.created_at,
                       (SELECT count(*) FROM zentriq.assets a WHERE a.department_id = d.id AND a.status = 'active'),
                       (SELECT count(*) FROM zentriq.nonit_assets n WHERE n.department_id = d.id AND n.status <> 'disposed')
                  FROM zentriq.departments d ORDER BY lower(d.name)
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), code = r.IsDBNull(2) ? null : r.GetString(2), description = r.IsDBNull(3) ? null : r.GetString(3),
                    createdAt = r.GetFieldValue<DateTimeOffset>(4), assets = r.GetInt64(5), nonItAssets = r.GetInt64(6),
                });
            await using var un = ds.CreateCommand("SELECT count(*) FROM zentriq.assets WHERE status = 'active' AND department_id IS NULL");
            return Results.Ok(new { items = list, unassigned = (long)(await un.ExecuteScalarAsync(ct))! });
        });

        g.MapPost("", async (DepartmentBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (Check(body) is { } problem) return problem;
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.departments (id, organization_id, name, code, description, created_by)
                VALUES (@id, (SELECT id FROM zentriq.organizations ORDER BY created_at LIMIT 1), @n, @c, @d, @u)
                """);
            Fill(cmd, id, body);
            cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A department with this name or code already exists."); }
            await rc.AuditAsync("department.create", body.Name.Trim());
            return Results.Created($"/api/v1/departments/{id}", new { id, name = body.Name.Trim() });
        }).RequireAuthorization(Permissions.AssetsManage);

        g.MapPut("/{id:guid}", async (Guid id, DepartmentBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (Check(body) is { } problem) return problem;
            await using var cmd = ds.CreateCommand("UPDATE zentriq.departments SET name = @n, code = @c, description = @d WHERE id = @id");
            Fill(cmd, id, body);
            try { if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A department with this name or code already exists."); }
            await rc.AuditAsync("department.update", body.Name.Trim(), detail: new { id });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AssetsManage);

        g.MapDelete("/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.departments WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("department.delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AssetsManage);

        // Assign (or clear, with departmentId = null) the department of one or more assets.
        api.MapPost("/assets/department", async (AssignBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (body.AssetIds is not { Length: > 0 } || body.AssetIds.Length > 10_000) return Validation.Problem("assetIds", "Select 1–10000 assets.");
            string? name = null;
            if (body.DepartmentId is { } dep)
            {
                await using var chk = ds.CreateCommand("SELECT name FROM zentriq.departments WHERE id = @id");
                chk.Parameters.AddWithValue("id", dep);
                name = await chk.ExecuteScalarAsync(ct) as string;
                if (name is null) return Validation.Problem("departmentId", "The department no longer exists.");
            }
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.assets SET department_id = @d, updated_at = now(), row_version = row_version + 1
                 WHERE id = ANY(@ids) AND department_id IS DISTINCT FROM @d
                """);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Uuid) { Value = (object?)body.DepartmentId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("ids", body.AssetIds.Distinct().ToArray());
            var n = await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("asset.department", name ?? "(none)", detail: new { assets = body.AssetIds.Length, changed = n, body.DepartmentId });
            return Results.Ok(new { updated = n, department = name });
        }).WithTags("Assets").RequireModule(Modules.Inventory).RequireAuthorization(Permissions.AssetsManage);
    }

    private static IResult? Check(DepartmentBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 100) return Validation.Problem("name", "Enter a department or unit name (up to 100 characters).");
        if (b.Code is { Length: > 30 }) return Validation.Problem("code", "The code can have at most 30 characters.");
        if (b.Description is { Length: > 500 }) return Validation.Problem("description", "The description can have at most 500 characters.");
        return null;
    }

    private static void Fill(NpgsqlCommand cmd, Guid id, DepartmentBody b)
    {
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("n", b.Name.Trim());
        cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(b.Code) ? DBNull.Value : b.Code.Trim() });
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(b.Description) ? DBNull.Value : b.Description.Trim() });
    }
}
