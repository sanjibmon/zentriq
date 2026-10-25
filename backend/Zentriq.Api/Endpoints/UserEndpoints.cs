using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Zentriq.Api.Infrastructure;
using Zentriq.Application.Security;
using Zentriq.Domain;
using Zentriq.Infrastructure.Data;

namespace Zentriq.Api.Endpoints;

public static class UserEndpoints
{
    public sealed record CreateUserBody(string Email, string DisplayName, string Password, string[] Roles, Guid[]? Groups = null);
    public sealed record UpdateUserBody(string? DisplayName, string? Status, string[]? Roles, string? NewPassword, Guid[]? Groups = null);

    public static void MapUserEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Users & roles");

        g.MapGet("/users", async (ZentriqDbContext db, Npgsql.NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var users = await db.Users.AsNoTracking().Include(u => u.Roles).ThenInclude(r => r.Role).OrderBy(u => u.DisplayName).ToListAsync(ct);
            // Groups of each user, and the roles they get through them.
            var groups = new Dictionary<Guid, List<object>>();
            await using (var cmd = ds.CreateCommand("""
                SELECT m.user_id, g.id, g.name, ARRAY(SELECT r.name FROM zentriq.user_group_roles gr JOIN zentriq.roles r ON r.id = gr.role_id WHERE gr.group_id = g.id ORDER BY 1)
                  FROM zentriq.user_group_members m JOIN zentriq.user_groups g ON g.id = m.group_id ORDER BY g.name
                """))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    var uid = r.GetGuid(0);
                    (groups.TryGetValue(uid, out var gl) ? gl : groups[uid] = []).Add(new { id = r.GetGuid(1), name = r.GetString(2), roles = r.GetFieldValue<string[]>(3) });
                }
            return Results.Ok(users.Select(u => new
            {
                u.Id, u.Email, u.DisplayName, u.Status, u.MfaEnabled, u.LastLoginAt, u.CreatedAt,
                locked = u.LockedUntil > DateTimeOffset.UtcNow,
                roles = u.Roles.Select(r => new { id = r.RoleId, name = r.Role!.Name }),
                groups = groups.TryGetValue(u.Id, out var gs) ? gs : [],
            }));
        }).RequireAuthorization(Permissions.UsersManage);

        g.MapPost("/users", async (CreateUserBody body, ZentriqDbContext db, Npgsql.NpgsqlDataSource ds, IPasswordHasher<User> hasher, RequestContext rc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email) || !body.Email.Contains('@')) return Validation.Problem("email", "Enter a valid e-mail address.");
            if (string.IsNullOrWhiteSpace(body.DisplayName)) return Validation.Problem("displayName", "Name is required.");
            var pwErr = PasswordPolicy.Validate(body.Password, body.Email);
            if (pwErr is not null) return Validation.Problem("password", pwErr);
            var roles = (body.Roles ?? []).Distinct().ToArray();
            if (roles.Length == 0 && (body.Groups ?? []).Length == 0) return Validation.Problem("roles", "Assign at least one role or group.");
            if (await RoleEndpoints.GrantProblemAsync(ds, rc, roles, ct) is { } denied) return denied;
            var email = body.Email.Trim();
            if (await db.Users.AnyAsync(u => u.Email.ToLower() == email.ToLower(), ct)) return Results.Problem(statusCode: 409, title: "A user with this e-mail already exists.");
            var now = DateTimeOffset.UtcNow;
            var u = new User { Id = Guid.NewGuid(), Email = email, DisplayName = body.DisplayName.Trim(), SecurityStamp = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now, PasswordChangedAt = now };
            u.PasswordHash = hasher.HashPassword(u, body.Password);
            db.Users.Add(u);
            foreach (var r in roles) db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = r });
            await db.SaveChangesAsync(ct);
            if (body.Groups is { Length: > 0 } gs)
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO zentriq.user_group_members (group_id, user_id) SELECT g.id, {u.Id} FROM zentriq.user_groups g WHERE g.id = ANY({gs})", ct);
            await rc.AuditAsync("user.create", u.Email, detail: new { roles, groups = body.Groups });
            return Results.Created($"/api/v1/users/{u.Id}", new { u.Id, u.Email });
        }).RequireAuthorization(Permissions.UsersManage);

        g.MapPatch("/users/{id:guid}", async (Guid id, UpdateUserBody body, ZentriqDbContext db, Npgsql.NpgsqlDataSource ds, IPasswordHasher<User> hasher, RequestContext rc, CancellationToken ct) =>
        {
            var u = await db.Users.Include(x => x.Roles).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u is null) return Results.NotFound();
            var changes = new List<string>();
            if (body.DisplayName is { } dn && !string.IsNullOrWhiteSpace(dn)) { u.DisplayName = dn.Trim(); changes.Add("displayName"); }
            if (body.Status is { } st)
            {
                if (st is not ("active" or "disabled")) return Validation.Problem("status", "Status must be active or disabled.");
                if (st == "disabled" && id == rc.UserId) return Validation.Problem("status", "You cannot disable your own account.");
                u.Status = st; changes.Add("status");
            }
            var accessChanged = false;
            if (body.Roles is { } roles)
            {
                roles = roles.Distinct().ToArray();
                // Roles the user already has may stay; new ones must be within the editor's own rights.
                var added = roles.Where(r => u.Roles.All(x => x.RoleId != r)).ToArray();
                if (await RoleEndpoints.GrantProblemAsync(ds, rc, added, ct) is { } denied) return denied;
                var known = await db.Roles.AsNoTracking().Select(r => r.Id).ToListAsync(ct);
                if (roles.Any(r => !known.Contains(r))) return Validation.Problem("roles", "Unknown role.");
                db.UserRoles.RemoveRange(u.Roles);
                foreach (var r in roles) db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = r });
                changes.Add("roles");
                accessChanged = true;
            }
            if (body.NewPassword is { } np)
            {
                var err = PasswordPolicy.Validate(np, u.Email);
                if (err is not null) return Validation.Problem("newPassword", err);
                u.PasswordHash = hasher.HashPassword(u, np); u.PasswordChangedAt = DateTimeOffset.UtcNow; u.LockedUntil = null; u.FailedAttempts = 0;
                changes.Add("password");
            }
            // A new password or a disabled account ends existing sessions; changed roles or groups only reload the permissions.
            if (changes.Contains("password") || changes.Contains("status")) u.SecurityStamp = Guid.NewGuid();
            if (body.Groups is not null) { accessChanged = true; changes.Add("groups"); }
            if (accessChanged) u.AccessStamp = Guid.NewGuid();
            u.UpdatedAt = DateTimeOffset.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            if (body.Groups is { } groups)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM zentriq.user_group_members WHERE user_id = {u.Id}", ct);
                if (groups.Length > 0)
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO zentriq.user_group_members (group_id, user_id) SELECT g.id, {u.Id} FROM zentriq.user_groups g WHERE g.id = ANY({groups})", ct);
            }

            // Never leave the platform without an active administrator (directly or through a group).
            var admins = await db.Database.SqlQuery<long>($"SELECT count(DISTINCT g.user_id) AS \"Value\" FROM zentriq.user_role_grants g JOIN zentriq.users x ON x.id = g.user_id WHERE g.role_id = 'admin' AND x.status = 'active'").SingleAsync(ct);
            if (admins == 0)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: 409, title: "At least one active administrator is required.");
            }
            await tx.CommitAsync(ct);
            await rc.AuditAsync("user.update", u.Email, detail: new { changes, roles = body.Roles, groups = body.Groups });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.UsersManage);

    }
}
