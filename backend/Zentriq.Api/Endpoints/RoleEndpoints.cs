using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Roles (built-in and custom) and user groups. A role is a set of permissions; a group carries roles for all its members.
/// What a user may do is the union of the permissions of their own roles and of their groups' roles. Nobody can give a
/// role more than they may do themselves, and the platform always keeps an active administrator.
/// </summary>
public static partial class RoleEndpoints
{
    public sealed record RoleBody(string? Name, string? Description, string[]? Permissions);
    public sealed record GroupBody(string? Name, string? Description, string[]? Roles, Guid[]? Members);

    public static void MapRoleEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Users & roles").RequireAuthorization(Permissions.UsersManage);

        g.MapGet("/roles", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT r.id, r.name, r.description, r.built_in,
                       ARRAY(SELECT rp.permission_code FROM zentriq.role_permissions rp WHERE rp.role_id = r.id ORDER BY 1),
                       (SELECT count(*) FROM zentriq.user_roles ur WHERE ur.role_id = r.id),
                       (SELECT count(*) FROM zentriq.user_group_roles gr WHERE gr.role_id = r.id),
                       (SELECT count(DISTINCT g.user_id) FROM zentriq.user_role_grants g JOIN zentriq.users u ON u.id = g.user_id AND u.status = 'active' WHERE g.role_id = r.id)
                  FROM zentriq.roles r
                 ORDER BY r.built_in DESC, CASE r.id WHEN 'admin' THEN 0 ELSE 1 END, r.name
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetString(0), name = r.GetString(1), description = r.GetString(2), builtIn = r.GetBoolean(3), permissions = r.GetFieldValue<string[]>(4),
                    users = r.GetInt64(5), groups = r.GetInt64(6), activeMembers = r.GetInt64(7),
                });
            return Results.Ok(list);
        });

        g.MapGet("/permissions", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT code, description FROM zentriq.permissions ORDER BY code");
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(new { code = r.GetString(0), description = r.GetString(1) });
            return Results.Ok(list);
        });

        g.MapPost("/roles", async (RoleBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (await CheckRoleAsync(body, ds, rc, null, ct) is { } bad) return bad;
            var id = NewRoleId(body.Name!);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var ins = new NpgsqlCommand("INSERT INTO zentriq.roles (id, name, description, built_in, created_by) VALUES (@id, @n, @d, false, @by)", conn, tx))
            {
                ins.Parameters.AddWithValue("id", id);
                ins.Parameters.AddWithValue("n", body.Name!.Trim());
                ins.Parameters.AddWithValue("d", (body.Description ?? "").Trim());
                ins.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
                try { await ins.ExecuteNonQueryAsync(ct); }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A role with this name exists."); }
            }
            await SetPermissionsAsync(conn, tx, id, body.Permissions!, ct);
            await tx.CommitAsync(ct);
            await rc.AuditAsync("role.create", body.Name!.Trim(), detail: new { id, permissions = body.Permissions });
            return Results.Ok(new { id });
        });

        g.MapPut("/roles/{id}", async (string id, RoleBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (await BuiltInAsync(ds, id, ct) is not { } builtIn) return Results.NotFound();
            if (builtIn) return Results.Problem(statusCode: 409, title: "Built-in roles cannot be changed. Duplicate the role and change the copy.");
            if (await CheckRoleAsync(body, ds, rc, id, ct) is { } bad) return bad;
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var up = new NpgsqlCommand("UPDATE zentriq.roles SET name = @n, description = @d, updated_at = now() WHERE id = @id", conn, tx))
            {
                up.Parameters.AddWithValue("id", id);
                up.Parameters.AddWithValue("n", body.Name!.Trim());
                up.Parameters.AddWithValue("d", (body.Description ?? "").Trim());
                try { await up.ExecuteNonQueryAsync(ct); }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A role with this name exists."); }
            }
            await SetPermissionsAsync(conn, tx, id, body.Permissions!, ct);
            await TouchRoleMembersAsync(conn, tx, [id], ct);
            await tx.CommitAsync(ct);
            await rc.AuditAsync("role.update", body.Name!.Trim(), detail: new { id, permissions = body.Permissions });
            return Results.NoContent();
        });

        g.MapDelete("/roles/{id}", async (string id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (await BuiltInAsync(ds, id, ct) is not { } builtIn) return Results.NotFound();
            if (builtIn) return Results.Problem(statusCode: 409, title: "Built-in roles cannot be deleted.");
            await using (var used = ds.CreateCommand("""
                SELECT (SELECT count(*) FROM zentriq.user_roles WHERE role_id = @id), (SELECT count(*) FROM zentriq.user_group_roles WHERE role_id = @id)
                """))
            {
                used.Parameters.AddWithValue("id", id);
                await using var r = await used.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                if (r.GetInt64(0) + r.GetInt64(1) > 0)
                    return Results.Problem(statusCode: 409, title: $"The role is still given to {r.GetInt64(0)} user(s) and {r.GetInt64(1)} group(s). Take it away from them first.");
            }
            await using var del = ds.CreateCommand("DELETE FROM zentriq.roles WHERE id = @id AND NOT built_in RETURNING name");
            del.Parameters.AddWithValue("id", id);
            var name = await del.ExecuteScalarAsync(ct) as string;
            await rc.AuditAsync("role.delete", name, detail: new { id });
            return Results.NoContent();
        });

        // ---- groups -----------------------------------------------------------------------------------------------

        g.MapGet("/groups", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT g.id, g.name, g.description, g.created_at,
                       COALESCE((SELECT jsonb_agg(jsonb_build_object('id', r.id, 'name', r.name) ORDER BY r.name) FROM zentriq.user_group_roles gr JOIN zentriq.roles r ON r.id = gr.role_id WHERE gr.group_id = g.id), '[]'::jsonb)::text,
                       COALESCE((SELECT jsonb_agg(jsonb_build_object('id', u.id, 'displayName', u.display_name, 'email', u.email, 'status', u.status) ORDER BY u.display_name) FROM zentriq.user_group_members m JOIN zentriq.users u ON u.id = m.user_id WHERE m.group_id = g.id), '[]'::jsonb)::text
                  FROM zentriq.user_groups g ORDER BY lower(g.name)
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), description = r.GetString(2), createdAt = r.GetFieldValue<DateTimeOffset>(3),
                    roles = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(r.GetString(4)),
                    members = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(r.GetString(5)),
                });
            return Results.Ok(list);
        });

        g.MapPost("/groups", async (GroupBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
            await SaveGroupAsync(null, body, ds, rc, ct));
        g.MapPut("/groups/{id:guid}", async (Guid id, GroupBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
            await SaveGroupAsync(id, body, ds, rc, ct));

        g.MapDelete("/groups/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await TouchGroupMembersAsync(conn, tx, id, ct);
            await using var del = new NpgsqlCommand("DELETE FROM zentriq.user_groups WHERE id = @id RETURNING name", conn, tx);
            del.Parameters.AddWithValue("id", id);
            if (await del.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            if (await AdminsAsync(conn, tx, ct) == 0)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: 409, title: "At least one active administrator is required: this group gives the only administrators their rights.");
            }
            await tx.CommitAsync(ct);
            await rc.AuditAsync("group.delete", name, detail: new { id });
            return Results.NoContent();
        });
    }

    private static async Task<IResult> SaveGroupAsync(Guid? id, GroupBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct)
    {
        var name = body.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return Validation.Problem("name", "Enter a name (max 100 characters).");
        var roles = (body.Roles ?? []).Distinct().ToArray();
        var members = (body.Members ?? []).Distinct().ToArray();
        if (await GrantProblemAsync(ds, rc, roles, ct) is { } bad) return bad;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var gid = id ?? Guid.NewGuid();
        if (id is not null) await TouchGroupMembersAsync(conn, tx, gid, ct);   // members before the change
        await using (var up = new NpgsqlCommand(id is null
            ? "INSERT INTO zentriq.user_groups (id, name, description, created_by) VALUES (@id, @n, @d, @by)"
            : "UPDATE zentriq.user_groups SET name = @n, description = @d, updated_at = now() WHERE id = @id", conn, tx))
        {
            up.Parameters.AddWithValue("id", gid);
            up.Parameters.AddWithValue("n", name);
            up.Parameters.AddWithValue("d", (body.Description ?? "").Trim());
            up.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            try { if (await up.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A group with this name exists."); }
        }
        await using (var set = new NpgsqlCommand("""
            DELETE FROM zentriq.user_group_roles WHERE group_id = @id;
            INSERT INTO zentriq.user_group_roles (group_id, role_id) SELECT @id, r.id FROM zentriq.roles r WHERE r.id = ANY(@roles);
            DELETE FROM zentriq.user_group_members WHERE group_id = @id;
            INSERT INTO zentriq.user_group_members (group_id, user_id) SELECT @id, u.id FROM zentriq.users u WHERE u.id = ANY(@members);
            """, conn, tx))
        {
            set.Parameters.AddWithValue("id", gid);
            set.Parameters.Add(new NpgsqlParameter("roles", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = roles });
            set.Parameters.Add(new NpgsqlParameter("members", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = members });
            await set.ExecuteNonQueryAsync(ct);
        }
        await TouchGroupMembersAsync(conn, tx, gid, ct);   // members after the change
        if (await AdminsAsync(conn, tx, ct) == 0)
        {
            await tx.RollbackAsync(ct);
            return Results.Problem(statusCode: 409, title: "At least one active administrator is required.");
        }
        await tx.CommitAsync(ct);
        await rc.AuditAsync(id is null ? "group.create" : "group.update", name, detail: new { id = gid, roles, members = members.Length });
        return id is null ? Results.Ok(new { id = gid }) : Results.NoContent();
    }

    private static async Task<IResult?> CheckRoleAsync(RoleBody body, NpgsqlDataSource ds, RequestContext rc, string? id, CancellationToken ct)
    {
        var name = body.Name?.Trim() ?? "";
        if (name.Length is 0 or > 80) return Validation.Problem("name", "Enter a name (max 80 characters).");
        if ((body.Description ?? "").Length > 300) return Validation.Problem("description", "Description: max 300 characters.");
        var perms = body.Permissions ?? [];
        if (perms.Length == 0) return Validation.Problem("permissions", "Choose at least one permission.");
        if (perms.FirstOrDefault(p => !Permissions.All.Contains(p)) is { } unknown) return Validation.Problem("permissions", $"Unknown permission '{unknown}'.");
        // No privilege escalation: a role cannot get permissions its author does not have.
        if (perms.FirstOrDefault(p => !rc.Has(p)) is { } missing)
            return Results.Problem(statusCode: 403, title: $"You cannot give a role the permission '{missing}', because you do not have it yourself.");
        // A role that can see nothing would leave its users with an empty console.
        if (!perms.Any(p => p.EndsWith(".view", StringComparison.Ordinal) || p is Permissions.UsersManage or Permissions.SettingsManage or Permissions.RemoteStart or Permissions.ScansRun))
            return Validation.Problem("permissions", "Give the role at least one area it can see (for example Non-IT assets › View).");
        await Task.CompletedTask;
        return null;
    }

    /// <summary>Roles (for users or groups) can only be given by someone who has all their permissions.</summary>
    public static async Task<IResult?> GrantProblemAsync(NpgsqlDataSource ds, RequestContext rc, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        if (roles.Count == 0) return null;
        await using var cmd = ds.CreateCommand("""
            SELECT r.id, r.name, ARRAY(SELECT rp.permission_code FROM zentriq.role_permissions rp WHERE rp.role_id = r.id) FROM zentriq.roles r WHERE r.id = ANY(@ids)
            """);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = roles.ToArray() });
        var found = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            found++;
            if (r.GetFieldValue<string[]>(2).FirstOrDefault(p => !rc.Has(p)) is { } missing)
                return Results.Problem(statusCode: 403, title: $"You cannot give the role '{r.GetString(1)}': it includes '{missing}', which you do not have yourself.");
        }
        return found == roles.Count ? null : Validation.Problem("roles", "Unknown role.");
    }

    private static async Task<bool?> BuiltInAsync(NpgsqlDataSource ds, string id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT built_in FROM zentriq.roles WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) as bool?;
    }

    private static async Task SetPermissionsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string id, string[] perms, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM zentriq.role_permissions WHERE role_id = @id;
            INSERT INTO zentriq.role_permissions (role_id, permission_code) SELECT @id, p.code FROM zentriq.permissions p WHERE p.code = ANY(@perms);
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.Add(new NpgsqlParameter("perms", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = perms.Distinct().ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Users who have these roles (directly or through a group) reload their permissions at their next request.</summary>
    public static async Task TouchRoleMembersAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string[] roles, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE zentriq.users SET access_stamp = gen_random_uuid() WHERE id IN (SELECT user_id FROM zentriq.user_role_grants WHERE role_id = ANY(@r))", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = roles });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task TouchGroupMembersAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid group, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE zentriq.users SET access_stamp = gen_random_uuid() WHERE id IN (SELECT user_id FROM zentriq.user_group_members WHERE group_id = @g)", conn, tx);
        cmd.Parameters.AddWithValue("g", group);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Active users who are administrators, directly or through a group.</summary>
    public static async Task<long> AdminsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT count(DISTINCT g.user_id) FROM zentriq.user_role_grants g JOIN zentriq.users u ON u.id = g.user_id WHERE g.role_id = 'admin' AND u.status = 'active'
            """, conn, tx);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static string NewRoleId(string name)
    {
        var slug = SlugChars().Replace(name.Trim().ToLowerInvariant(), "_").Trim('_');
        if (slug.Length > 30) slug = slug[..30];
        return $"custom_{slug}_{Guid.NewGuid().ToString("N")[..6]}";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugChars();
}
