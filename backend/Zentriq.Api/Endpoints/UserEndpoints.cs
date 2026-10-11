using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Zentriq.Api.Infrastructure;
using Zentriq.Application.Security;
using Zentriq.Domain;
using Zentriq.Infrastructure.Data;

namespace Zentriq.Api.Endpoints;

public static class UserEndpoints
{
    public sealed record CreateUserBody(string Email, string DisplayName, string Password, string[] Roles);
    public sealed record UpdateUserBody(string? DisplayName, string? Status, string[]? Roles, string? NewPassword);

    public static void MapUserEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("").WithTags("Users & roles");

        g.MapGet("/users", async (ZentriqDbContext db, CancellationToken ct) =>
        {
            var users = await db.Users.AsNoTracking().Include(u => u.Roles).ThenInclude(r => r.Role).OrderBy(u => u.DisplayName).ToListAsync(ct);
            return Results.Ok(users.Select(u => new
            {
                u.Id, u.Email, u.DisplayName, u.Status, u.MfaEnabled, u.LastLoginAt, u.CreatedAt,
                locked = u.LockedUntil > DateTimeOffset.UtcNow,
                roles = u.Roles.Select(r => new { id = r.RoleId, name = r.Role!.Name }),
            }));
        }).RequireAuthorization(Permissions.UsersManage);

        g.MapPost("/users", async (CreateUserBody body, ZentriqDbContext db, IPasswordHasher<User> hasher, RequestContext rc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email) || !body.Email.Contains('@')) return Validation.Problem("email", "Enter a valid e-mail address.");
            if (string.IsNullOrWhiteSpace(body.DisplayName)) return Validation.Problem("displayName", "Name is required.");
            var pwErr = PasswordPolicy.Validate(body.Password, body.Email);
            if (pwErr is not null) return Validation.Problem("password", pwErr);
            var roles = body.Roles ?? [];
            if (roles.Length == 0) return Validation.Problem("roles", "Assign at least one role.");
            var known = await db.Roles.AsNoTracking().Select(r => r.Id).ToListAsync(ct);
            if (roles.Any(r => !known.Contains(r))) return Validation.Problem("roles", "Unknown role.");
            var email = body.Email.Trim();
            if (await db.Users.AnyAsync(u => u.Email.ToLower() == email.ToLower(), ct)) return Results.Problem(statusCode: 409, title: "A user with this e-mail already exists.");
            var now = DateTimeOffset.UtcNow;
            var u = new User { Id = Guid.NewGuid(), Email = email, DisplayName = body.DisplayName.Trim(), SecurityStamp = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now, PasswordChangedAt = now };
            u.PasswordHash = hasher.HashPassword(u, body.Password);
            db.Users.Add(u);
            foreach (var r in roles.Distinct()) db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = r });
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("user.create", u.Email, detail: new { roles });
            return Results.Created($"/api/v1/users/{u.Id}", new { u.Id, u.Email });
        }).RequireAuthorization(Permissions.UsersManage);

        g.MapPatch("/users/{id:guid}", async (Guid id, UpdateUserBody body, ZentriqDbContext db, IPasswordHasher<User> hasher, RequestContext rc, CancellationToken ct) =>
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
            if (body.Roles is { } roles)
            {
                var known = await db.Roles.AsNoTracking().Select(r => r.Id).ToListAsync(ct);
                if (roles.Length == 0 || roles.Any(r => !known.Contains(r))) return Validation.Problem("roles", "Assign at least one valid role.");
                db.UserRoles.RemoveRange(u.Roles);
                foreach (var r in roles.Distinct()) db.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = r });
                changes.Add("roles");
            }
            if (body.NewPassword is { } np)
            {
                var err = PasswordPolicy.Validate(np, u.Email);
                if (err is not null) return Validation.Problem("newPassword", err);
                u.PasswordHash = hasher.HashPassword(u, np); u.PasswordChangedAt = DateTimeOffset.UtcNow; u.LockedUntil = null; u.FailedAttempts = 0;
                changes.Add("password");
            }
            u.SecurityStamp = Guid.NewGuid(); // revoke existing sessions after any security-relevant change
            u.UpdatedAt = DateTimeOffset.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);

            // Never leave the platform without an active administrator.
            var admins = await db.UserRoles.CountAsync(r => r.RoleId == "admin" && db.Users.Any(x => x.Id == r.UserId && x.Status == "active"), ct);
            if (admins == 0)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: 409, title: "At least one active administrator is required.");
            }
            await tx.CommitAsync(ct);
            await rc.AuditAsync("user.update", u.Email, detail: new { changes, roles = body.Roles });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.UsersManage);

        g.MapGet("/roles", async (ZentriqDbContext db, CancellationToken ct) =>
        {
            var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderBy(r => r.Name).ToListAsync(ct);
            return Results.Ok(roles.Select(r => new { r.Id, r.Name, r.Description, r.BuiltIn, permissions = r.Permissions.Select(p => p.PermissionCode).OrderBy(x => x) }));
        }).RequireAuthorization(Permissions.UsersManage);

        g.MapGet("/permissions", async (ZentriqDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Permissions.AsNoTracking().OrderBy(p => p.Code).ToListAsync(ct)))
            .RequireAuthorization(Permissions.UsersManage);
    }
}
