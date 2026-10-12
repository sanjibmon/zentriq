using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Zentriq.Api.Infrastructure;
using Zentriq.Application.Security;
using Zentriq.Infrastructure.Audit;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Licensing;

namespace Zentriq.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginBody(string Email, string Password, string? MfaCode);
    public sealed record ChangePasswordBody(string CurrentPassword, string NewPassword);
    public sealed record MfaConfirmBody(string Code);

    private const string MfaPurpose = "Zentriq.Mfa.v1";

    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth").WithTags("Authentication");

        g.MapPost("/login", async (LoginBody body, ZentriqDbContext db, IPasswordHasher<User> hasher, AuditWriter audit,
            IDataProtectionProvider dp, LicensingService licensing, HttpContext http, CancellationToken ct) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString();
            var email = (body.Email ?? "").Trim();
            var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower(), ct);
            var now = DateTimeOffset.UtcNow;
            var threshold = await licensing.GetSettingAsync("security.lockout_threshold", 5, ct);
            var lockMinutes = await licensing.GetSettingAsync("security.lockout_minutes", 15, ct);

            IResult Fail(string reason)
            {
                _ = audit.WriteAsync(new AuditActor(user?.Id, email.Length > 0 ? email : "unknown", ip), "auth.login", email, "Failure", new { reason });
                return Results.Problem(statusCode: 401, title: "The e-mail address or password is incorrect, or the account is locked.");
            }

            if (user is null || user.Status != "active") return Fail(user is null ? "unknown-user" : "disabled");
            if (user.LockedUntil is { } lu && lu > now) return Fail("locked");

            var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, body.Password ?? "");
            if (verify == PasswordVerificationResult.Failed)
            {
                user.FailedAttempts++;
                if (user.FailedAttempts >= threshold) { user.LockedUntil = now.AddMinutes(lockMinutes); user.FailedAttempts = 0; }
                await db.SaveChangesAsync(ct);
                return Fail("bad-password");
            }
            if (verify == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = hasher.HashPassword(user, body.Password!);

            var mfaRequired = (await db.SystemSettings.AsNoTracking().Where(s => s.Key == "security.mfa_required").Select(s => s.Value).FirstOrDefaultAsync(ct))?.Trim() == "true";
            if (user.MfaEnabled)
            {
                if (string.IsNullOrEmpty(body.MfaCode))
                    return Results.Json(new { mfaRequired = true }, statusCode: 401);
                var secret = dp.CreateProtector(MfaPurpose).Unprotect(Convert.FromBase64String(user.MfaSecretProtected!));
                if (!Totp.Verify(secret, body.MfaCode, now)) { user.FailedAttempts++; await db.SaveChangesAsync(ct); return Fail("bad-mfa"); }
            }

            user.FailedAttempts = 0; user.LockedUntil = null; user.LastLoginAt = now;
            await db.SaveChangesAsync(ct);
            // When MFA is mandatory and this user has not set it up, the session carries no permissions until enrollment is confirmed.
            await SignInAsync(http, db, user, ct, mfaPending: mfaRequired && !user.MfaEnabled);
            await audit.WriteAsync(new AuditActor(user.Id, user.Email, ip), "auth.login", user.Email, "Success", ct: ct);
            return Results.Ok(new { ok = true, mfaEnrollmentRequired = mfaRequired && !user.MfaEnabled });
        }).AllowAnonymous().RequireRateLimiting("login");

        g.MapPost("/logout", async (HttpContext http, RequestContext rc) =>
        {
            if (rc.UserId is not null) await rc.AuditAsync("auth.logout", rc.UserName);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        g.MapGet("/me", async (RequestContext rc, ZentriqDbContext db, CancellationToken ct) =>
        {
            var u = await db.Users.AsNoTracking().Include(x => x.Roles).ThenInclude(r => r.Role).FirstAsync(x => x.Id == rc.UserId, ct);
            var lic = await rc.LicenseAsync(ct);
            return Results.Ok(new
            {
                id = u.Id, email = u.Email, displayName = u.DisplayName, mfaEnabled = u.MfaEnabled,
                mfaEnrollmentRequired = rc.Principal.HasClaim(c => c.Type == ZentriqClaims.MfaPending),
                roles = u.Roles.Select(r => new { id = r.RoleId, name = r.Role!.Name }),
                permissions = rc.Principal.FindAll(ZentriqClaims.Permission).Select(c => c.Value).OrderBy(x => x),
                license = new { mode = lic.Mode.ToString(), lic.Summary, lic.ExpiresAt, lic.DaysRemaining, featuresEnabled = lic.FeaturesEnabled, lic.Modules },
            });
        }).RequireAuthorization();

        g.MapPost("/password", async (ChangePasswordBody body, RequestContext rc, ZentriqDbContext db, IPasswordHasher<User> hasher, HttpContext http, CancellationToken ct) =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == rc.UserId, ct);
            if (hasher.VerifyHashedPassword(u, u.PasswordHash, body.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
                return Validation.Problem("currentPassword", "Current password is incorrect.");
            var err = PasswordPolicy.Validate(body.NewPassword, u.Email);
            if (err is not null) return Validation.Problem("newPassword", err);
            u.PasswordHash = hasher.HashPassword(u, body.NewPassword);
            u.PasswordChangedAt = DateTimeOffset.UtcNow; u.SecurityStamp = Guid.NewGuid(); u.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("auth.password_changed", u.Email);
            await SignInAsync(http, db, u, ct); // keep this session; others are revoked by the new stamp
            return Results.NoContent();
        }).RequireAuthorization();

        g.MapPost("/mfa/enroll", async (RequestContext rc, ZentriqDbContext db, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == rc.UserId, ct);
            if (u.MfaEnabled) return Results.Problem(statusCode: 409, title: "MFA is already enabled.");
            var secret = Totp.NewSecret();
            u.MfaSecretProtected = Convert.ToBase64String(dp.CreateProtector(MfaPurpose).Protect(secret));
            await db.SaveChangesAsync(ct);
            var uri = Totp.OtpAuthUri("Zentriq", u.Email, secret);
            return Results.Ok(new { secret = Totp.ToBase32(secret), otpauthUri = uri, qrSvg = AccountEndpoints.QrSvg(uri) });
        }).RequireAuthorization();

        g.MapPost("/mfa/confirm", async (MfaConfirmBody body, RequestContext rc, ZentriqDbContext db, IDataProtectionProvider dp, HttpContext http, CancellationToken ct) =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == rc.UserId, ct);
            if (u.MfaSecretProtected is null) return Results.Problem(statusCode: 400, title: "Start MFA enrollment first.");
            var secret = dp.CreateProtector(MfaPurpose).Unprotect(Convert.FromBase64String(u.MfaSecretProtected));
            if (!Totp.Verify(secret, body.Code, DateTimeOffset.UtcNow)) return Validation.Problem("code", "The code is not valid. Check the time on your device.");
            u.MfaEnabled = true; u.SecurityStamp = Guid.NewGuid(); u.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("auth.mfa_enabled", u.Email);
            await SignInAsync(http, db, u, ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    public static async Task SignInAsync(HttpContext http, ZentriqDbContext db, User user, CancellationToken ct, bool mfaPending = false)
    {
        var roleIds = await db.UserRoles.AsNoTracking().Where(r => r.UserId == user.Id).Select(r => r.RoleId).ToListAsync(ct);
        var perms = await db.RolePermissions.AsNoTracking().Where(rp => roleIds.Contains(rp.RoleId)).Select(rp => rp.PermissionCode).Distinct().ToListAsync(ct);
        var claims = new List<Claim>
        {
            new(ZentriqClaims.UserId, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.DisplayName),
            new(ZentriqClaims.Stamp, user.SecurityStamp.ToString()),
        };
        claims.AddRange(roleIds.Select(r => new Claim(ZentriqClaims.Role, r)));
        if (mfaPending) claims.Add(new Claim(ZentriqClaims.MfaPending, "1"));
        else claims.AddRange(perms.Select(p => new Claim(ZentriqClaims.Permission, p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = false });
    }
}
