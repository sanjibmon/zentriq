using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Zentriq.Api.Infrastructure;
using Zentriq.Application.Security;
using System.Text.Json;
using Npgsql;
using Zentriq.Infrastructure.Audit;
using Zentriq.Infrastructure.Auth;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Licensing;

namespace Zentriq.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginBody(string Email, string Password, string? MfaCode, bool? Directory = null);
    private sealed record OidcState(string State, string Nonce, string Verifier, DateTimeOffset Expires);
    private const string OidcCookie = "zentriq.oidc";
    private const string OidcPurpose = "Zentriq.Oidc.v1";

    /// <summary>The redirect URI registered in the Entra app: this console's address.</summary>
    public static string RedirectUri(HttpContext http) => $"https://{http.Request.Host}/api/v1/auth/oidc/callback";

    private static async Task<string?> AuthSourceAsync(NpgsqlDataSource ds, Guid userId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT auth_source FROM zentriq.users WHERE id = @u");
        cmd.Parameters.AddWithValue("u", userId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// A small page that continues to the console (a same-site navigation, so the new session cookie — SameSite=Strict —
    /// is sent with it).
    /// </summary>
    private static IResult Html(string to)
    {
        var url = System.Net.WebUtility.HtmlEncode(to);
        return Results.Content($"<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url={url}\"><title>Zentriq</title></head><body><a href=\"{url}\">Continue to Zentriq</a></body></html>", "text/html");
    }

    private static IResult SsoError(string message) => Html("/?ssoError=" + Uri.EscapeDataString(message));
    public sealed record ChangePasswordBody(string CurrentPassword, string NewPassword);
    public sealed record MfaConfirmBody(string Code);

    private const string MfaPurpose = "Zentriq.Mfa.v1";

    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth").WithTags("Authentication");

        g.MapPost("/login", async (LoginBody body, ZentriqDbContext db, IPasswordHasher<User> hasher, AuditWriter audit,
            IDataProtectionProvider dp, LicensingService licensing, SignInStore signIn, NpgsqlDataSource ds, HttpContext http, CancellationToken ct) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString();
            var email = (body.Email ?? "").Trim();
            var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower(), ct);
            var now = DateTimeOffset.UtcNow;
            var threshold = await licensing.GetSettingAsync("security.lockout_threshold", 5, ct);
            var lockMinutes = await licensing.GetSettingAsync("security.lockout_minutes", 15, ct);
            var source = user is null ? null : await AuthSourceAsync(ds, user.Id, ct);

            IResult Fail(string reason, string? title = null)
            {
                _ = audit.WriteAsync(new AuditActor(user?.Id, email.Length > 0 ? email : "unknown", ip), "auth.login", email, "Failure", new { reason, method = source ?? (body.Directory == true ? "ldap" : "local") });
                return Results.Problem(statusCode: 401, title: title ?? "The e-mail address or password is incorrect, or the account is locked.");
            }

            // Active Directory: chosen on the sign-in page, or the account signs in with the directory. DOMAIN\user names
            // are always directory names.
            var useDirectory = body.Directory == true || source == "ldap" || (user is null && email.Contains('\\'));
            if (useDirectory)
            {
                var ldap = await signIn.LdapAsync(ct);
                if (!ldap.Enabled) return Fail("directory-off", "Sign-in with Active Directory accounts is not turned on.");
                if (user is not null && user.LockedUntil is { } dlu && dlu > now) return Fail("locked");
                DirectoryIdentity who;
                try { who = await Task.Run(() => LdapSignIn.Authenticate(ldap, email, body.Password ?? ""), ct); }
                catch (DirectorySignInException ex)
                {
                    if (user is not null && ex.Reason == "bad-password")
                    {
                        user.FailedAttempts++;
                        if (user.FailedAttempts >= threshold) { user.LockedUntil = now.AddMinutes(lockMinutes); user.FailedAttempts = 0; }
                        await db.SaveChangesAsync(ct);
                    }
                    return Fail(ex.Reason, ex.Reason is "bad-password" or "empty" ? null : ex.Message);
                }
                try
                {
                    var outcome = await DirectoryUsers.ResolveAsync(ds, who, ldap.CreateUsers, ct);
                    db.ChangeTracker.Clear();
                    user = await db.Users.Include(u => u.Roles).FirstAsync(u => u.Id == outcome.UserId, ct);
                    source = "ldap";
                    if (outcome.Created) await audit.WriteAsync(new AuditActor(user.Id, user.Email, ip), "user.create", user.Email, "Success", new { method = "ldap", roles = outcome.Roles }, ct: ct);
                }
                catch (DirectorySignInException ex) { return Fail(ex.Reason, ex.Message); }
                if (user.LockedUntil is { } lu2 && lu2 > now) return Fail("locked");
            }
            else
            {
                if (user is null || user.Status != "active") return Fail(user is null ? "unknown-user" : "disabled");
                // Microsoft Entra ID accounts sign in with "Sign In With Microsoft".
                if (source == "entra") return Fail("entra-account", "This account signs in with Microsoft. Use Sign In With Microsoft.");
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
            }

            // Access rules: expiry, sign-in hours, networks (checked after the password, so they reveal nothing to others).
            if (user.Rules().Denied(now, http.Connection.RemoteIpAddress, TimeZoneInfo.Local) is { } denied)
                return Fail(denied, AccessRules.Message(denied));
            var mfaRequired = (await db.SystemSettings.AsNoTracking().Where(s => s.Key == "security.mfa_required").Select(s => s.Value).FirstOrDefaultAsync(ct))?.Trim() == "true"
                || await RoleRequiresMfaAsync(ds, user.Id, ct);
            if (user.MfaEnabled)
            {
                if (string.IsNullOrEmpty(body.MfaCode))
                    return Results.Json(new { mfaRequired = true }, statusCode: 401);
                var mfaKey = dp.CreateProtector(MfaPurpose).Unprotect(Convert.FromBase64String(user.MfaSecretProtected!));
                if (!Totp.Verify(mfaKey, body.MfaCode, now)) { user.FailedAttempts++; await db.SaveChangesAsync(ct); return Fail("bad-mfa"); }
            }

            user.FailedAttempts = 0; user.LockedUntil = null; user.LastLoginAt = now;
            await db.SaveChangesAsync(ct);
            // When MFA is mandatory and this user has not set it up, the session carries no permissions until enrollment is confirmed.
            await SignInAsync(http, db, user, ct, mfaPending: mfaRequired && !user.MfaEnabled);
            await audit.WriteAsync(new AuditActor(user.Id, user.Email, ip), "auth.login", user.Email, "Success", new { method = source ?? "local" }, ct: ct);
            return Results.Ok(new { ok = true, mfaEnrollmentRequired = mfaRequired && !user.MfaEnabled });
        }).AllowAnonymous().RequireRateLimiting("login");

        // ---- Sign In With Microsoft (Microsoft Entra ID, OpenID Connect) ---------------------------------------------

        g.MapGet("/oidc/start", async (string? hint, SignInStore signIn, EntraOidc oidc, IDataProtectionProvider dp, HttpContext http, CancellationToken ct) =>
        {
            var entra = await signIn.EntraAsync(ct);
            if (!entra.Enabled || !EntraOidc.ValidTenant(entra.TenantId) || entra.ClientId.Length == 0) return SsoError("Sign-in with Microsoft is not turned on.");
            var state = EntraOidc.NewToken();
            var nonce = EntraOidc.NewToken();
            var verifier = EntraOidc.NewToken() + EntraOidc.NewToken();
            var payload = JsonSerializer.Serialize(new OidcState(state, nonce, verifier, DateTimeOffset.UtcNow.AddMinutes(10)));
            http.Response.Cookies.Append(OidcCookie, dp.CreateProtector(OidcPurpose).Protect(payload), new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/api/v1/auth/oidc", MaxAge = TimeSpan.FromMinutes(10), IsEssential = true,
            });
            return Results.Redirect(oidc.AuthorizeUrl(entra, RedirectUri(http), state, nonce, verifier, hint is { Length: > 0 and < 320 } ? hint : null));
        }).AllowAnonymous().RequireRateLimiting("login");

        g.MapGet("/oidc/callback", async (string? code, string? state, string? error, string? error_description, SignInStore signIn, EntraOidc oidc,
            IDataProtectionProvider dp, NpgsqlDataSource ds, ZentriqDbContext db, AuditWriter audit, HttpContext http, CancellationToken ct) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString();
            var raw = http.Request.Cookies[OidcCookie];
            http.Response.Cookies.Delete(OidcCookie, new CookieOptions { Path = "/api/v1/auth/oidc", Secure = true, SameSite = SameSiteMode.Lax });
            async Task<IResult> Failed(string reason, string message, string? who = null)
            {
                await audit.WriteAsync(new AuditActor(null, who ?? "unknown", ip), "auth.login", who ?? "Microsoft", "Failure", new { reason, method = "entra", message }, ct: ct);
                return SsoError(message);
            }
            if (!string.IsNullOrEmpty(error)) return await Failed("provider-error", "Microsoft did not sign you in: " + (error_description ?? error));
            OidcState? st = null;
            try { if (raw is not null) st = JsonSerializer.Deserialize<OidcState>(dp.CreateProtector(OidcPurpose).Unprotect(raw)); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException) { }
            if (st is null || st.Expires < DateTimeOffset.UtcNow || string.IsNullOrEmpty(state) || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(st.State)))
                return await Failed("state", "The sign-in took too long or was started in another browser. Try again.");
            if (string.IsNullOrEmpty(code)) return await Failed("no-code", "Microsoft did not return a sign-in code.");
            var entra = await signIn.EntraAsync(ct);
            if (!entra.Enabled) return await Failed("entra-off", "Sign-in with Microsoft is not turned on.");
            var appKey = await signIn.EntraSecretAsync(ct);
            if (string.IsNullOrEmpty(appKey)) return await Failed("not-configured", "Sign-in with Microsoft is not set up completely (client secret missing).");
            DirectoryIdentity who;
            try { who = await oidc.SignInAsync(entra, appKey, code, RedirectUri(http), st.Verifier, st.Nonce, ct); }
            catch (DirectorySignInException ex) { return await Failed(ex.Reason, ex.Message); }
            DirectoryUsers.Outcome outcome;
            try { outcome = await DirectoryUsers.ResolveAsync(ds, who, entra.CreateUsers, ct); }
            catch (DirectorySignInException ex) { return await Failed(ex.Reason, ex.Message, who.Email); }
            var user = await db.Users.FirstAsync(u => u.Id == outcome.UserId, ct);
            if (user.LockedUntil is { } lu && lu > DateTimeOffset.UtcNow) return await Failed("locked", "The account is locked. Try again later.", user.Email);
            if (user.Rules().Denied(DateTimeOffset.UtcNow, http.Connection.RemoteIpAddress, TimeZoneInfo.Local) is { } denied)
                return await Failed(denied, AccessRules.Message(denied), user.Email);
            if (outcome.Created) await audit.WriteAsync(new AuditActor(user.Id, user.Email, ip), "user.create", user.Email, "Success", new { method = "entra", roles = outcome.Roles }, ct: ct);
            // Multi-factor authentication is Microsoft Entra ID's (Conditional Access / security defaults), not Zentriq's.
            user.FailedAttempts = 0; user.LastLoginAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await SignInAsync(http, db, user, ct);
            await audit.WriteAsync(new AuditActor(user.Id, user.Email, ip), "auth.login", user.Email, "Success", new { method = "entra", roles = outcome.Roles }, ct: ct);
            return Html("/");
        }).AllowAnonymous().RequireRateLimiting("login");

        g.MapPost("/logout", async (HttpContext http, RequestContext rc) =>
        {
            if (rc.UserId is not null) await rc.AuditAsync("auth.logout", rc.UserName);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        // The console calls this while the user is working without other requests, so the session sees the activity.
        g.MapGet("/alive", async (ZentriqDbContext db, CancellationToken ct) =>
            Results.Ok(new { idleSignoutMinutes = await IdleSetting.GetAsync(db, ct) })).RequireAuthorization();

        g.MapGet("/me", async (RequestContext rc, ZentriqDbContext db, CancellationToken ct) =>
        {
            var u = await db.Users.AsNoTracking().Include(x => x.Roles).ThenInclude(r => r.Role).FirstAsync(x => x.Id == rc.UserId, ct);
            var lic = await rc.LicenseAsync(ct);
            return Results.Ok(new
            {
                id = u.Id, email = u.Email, displayName = u.DisplayName, mfaEnabled = u.MfaEnabled,
                mfaEnrollmentRequired = rc.Principal.HasClaim(c => c.Type == ZentriqClaims.MfaPending),
                idleSignoutMinutes = await IdleSetting.GetAsync(db, ct),
                roles = u.Roles.Select(r => new { id = r.RoleId, name = r.Role!.Name }),
                permissions = rc.Principal.FindAll(ZentriqClaims.Permission).Select(c => c.Value).OrderBy(x => x),
                license = new { mode = lic.Mode.ToString(), lic.Summary, lic.ExpiresAt, lic.DaysRemaining, featuresEnabled = lic.FeaturesEnabled, lic.Modules, edition = lic.Edition,
                    sections = Zentriq.Domain.ProductSections.All.Select(x => new { id = x, title = Zentriq.Domain.ProductSections.Titles[x], installed = lic.InstalledSections.Contains(x), enabled = lic.SectionEnabled(x), standard = Zentriq.Domain.ProductSections.Standard.Contains(x) }) },
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
        var principal = await BuildPrincipalAsync(db, user, mfaPending, ct);
        var props = new AuthenticationProperties { IsPersistent = false };
        props.Items[SessionLifetime.Item] = SessionLifetime.Format(DateTimeOffset.UtcNow);
        props.Items[IdleSignout.Item] = SessionLifetime.Format(DateTimeOffset.UtcNow);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, props);
    }

    /// <summary>Whether one of the user's roles requires an authenticator app (Users, Roles &amp; Groups › role › Require MFA).</summary>
    public static async Task<bool> RoleRequiresMfaAsync(NpgsqlDataSource ds, Guid userId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT EXISTS (SELECT 1 FROM zentriq.user_role_grants g JOIN zentriq.roles r ON r.id = g.role_id WHERE g.user_id = @u AND r.mfa_required)");
        cmd.Parameters.AddWithValue("u", userId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The session identity: the user, the roles given directly and through groups, and their permissions.</summary>
    public static async Task<ClaimsPrincipal> BuildPrincipalAsync(ZentriqDbContext db, User user, bool mfaPending, CancellationToken ct)
    {
        var roleIds = await db.Database.SqlQuery<string>($"SELECT DISTINCT role_id AS \"Value\" FROM zentriq.user_role_grants WHERE user_id = {user.Id}").ToListAsync(ct);
        var perms = await db.RolePermissions.AsNoTracking().Where(rp => roleIds.Contains(rp.RoleId)).Select(rp => rp.PermissionCode).Distinct().ToListAsync(ct);
        var claims = new List<Claim>
        {
            new(ZentriqClaims.UserId, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.DisplayName),
            new(ZentriqClaims.Stamp, user.SecurityStamp.ToString()),
            new(ZentriqClaims.Access, user.AccessStamp.ToString()),
        };
        claims.AddRange(roleIds.Select(r => new Claim(ZentriqClaims.Role, r)));
        if (await db.Database.SqlQuery<bool>($"SELECT zentriq.user_is_scoped({user.Id}) AS \"Value\"").SingleAsync(ct)) claims.Add(new Claim(ZentriqClaims.Scoped, "1"));
        if (mfaPending) claims.Add(new Claim(ZentriqClaims.MfaPending, "1"));
        else claims.AddRange(perms.Select(p => new Claim(ZentriqClaims.Permission, p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
