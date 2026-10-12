using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using QRCoder;
using Zentriq.Api.Infrastructure;
using Zentriq.Application.Security;
using Zentriq.Domain;
using Zentriq.Infrastructure.Audit;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Mail;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Self-service account security: authenticator-app MFA (QR code, disable), forgotten password by e-mail,
/// and administrator MFA reset. Also the SMTP settings used to send reset e-mails.
/// </summary>
public static class AccountEndpoints
{
    public sealed record ForgotBody(string Email);
    public sealed record ResetBody(string Token, string NewPassword, string? MfaCode);
    public sealed record MfaDisableBody(string Password, string Code);
    public sealed record SmtpBody(bool Enabled, string? Host, int? Port, string? Security, string? Username, string? Password, bool? ClearPassword,
        string? FromAddress, string? FromName, string? ConsoleUrl);
    public sealed record SmtpTestBody(string To);

    internal const string MfaPurpose = "Zentriq.Mfa.v1";

    public static void MapAccountEndpoints(this RouteGroupBuilder api, ZentriqOptions options)
    {
        var g = api.MapGroup("/auth").WithTags("Authentication");

        // Lets the sign-in page decide whether to offer "Forgot password?" by e-mail.
        g.MapGet("/options", async (MailService mail, CancellationToken ct) =>
            Results.Ok(new { passwordResetByEmail = MailService.IsUsable(await mail.LoadAsync(ct)) })).AllowAnonymous();

        // QR code for the pending enrollment secret (rendered on the server; never cached).
        g.MapGet("/mfa/qr", async (RequestContext rc, ZentriqDbContext db, IDataProtectionProvider dp, HttpContext http, CancellationToken ct) =>
        {
            var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == rc.UserId, ct);
            if (u.MfaEnabled || u.MfaSecretProtected is null) return Results.NotFound();
            var secret = dp.CreateProtector(MfaPurpose).Unprotect(Convert.FromBase64String(u.MfaSecretProtected));
            http.Response.Headers.CacheControl = "no-store";
            return Results.Content(QrSvg(Totp.OtpAuthUri("Zentriq", u.Email, secret)), "image/svg+xml");
        }).RequireAuthorization();

        g.MapPost("/mfa/disable", async (MfaDisableBody body, RequestContext rc, ZentriqDbContext db, IPasswordHasher<User> hasher, IDataProtectionProvider dp,
            HttpContext http, CancellationToken ct) =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == rc.UserId, ct);
            if (!u.MfaEnabled) return Results.Problem(statusCode: 409, title: "MFA is not enabled.");
            var required = (await db.SystemSettings.AsNoTracking().Where(s => s.Key == "security.mfa_required").Select(s => s.Value).FirstOrDefaultAsync(ct))?.Trim() == "true";
            if (required) return Results.Problem(statusCode: 409, title: "MFA is required for all users by policy. Ask an administrator if you need to move to a new phone.");
            if (hasher.VerifyHashedPassword(u, u.PasswordHash, body.Password ?? "") == PasswordVerificationResult.Failed)
                return Validation.Problem("password", "Password is incorrect.");
            var secret = dp.CreateProtector(MfaPurpose).Unprotect(Convert.FromBase64String(u.MfaSecretProtected!));
            if (!Totp.Verify(secret, body.Code ?? "", DateTimeOffset.UtcNow)) return Validation.Problem("code", "The code is not valid.");
            u.MfaEnabled = false; u.MfaSecretProtected = null; u.SecurityStamp = Guid.NewGuid(); u.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("auth.mfa_disabled", u.Email);
            await AuthEndpoints.SignInAsync(http, db, u, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        // Always answers the same way so the form cannot be used to find out which e-mail addresses have accounts.
        g.MapPost("/forgot", async (ForgotBody body, ZentriqDbContext db, NpgsqlDataSource ds, MailService mail, AuditWriter audit, HttpContext http,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var generic = Results.Accepted(value: new { message = "If an active account uses this address, a password reset link has been sent to it. The link is valid for a limited time." });
            var email = (body.Email ?? "").Trim();
            var ip = http.Connection.RemoteIpAddress?.ToString();
            if (email.Length is 0 or > 320) return generic;
            var smtp = await mail.LoadAsync(ct);
            if (!MailService.IsUsable(smtp)) return Results.Problem(statusCode: 503, title: "Password reset by e-mail is not set up on this server. Ask a Zentriq administrator to reset your password.");
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower(), ct);
            if (user is null || user.Status != "active")
            {
                await audit.WriteAsync(new AuditActor(null, email, ip), "auth.password_reset_requested", email, "Failure", new { reason = user is null ? "unknown-user" : "disabled" }, ct: ct);
                return generic;
            }
            // At most 3 outstanding requests per user per hour.
            await using (var cnt = ds.CreateCommand("SELECT count(*) FROM zentriq.password_reset_tokens WHERE user_id = @u AND created_at > now() - interval '1 hour'"))
            {
                cnt.Parameters.AddWithValue("u", user.Id);
                if ((long)(await cnt.ExecuteScalarAsync(ct))! >= 3) return generic;
            }
            var minutes = await SettingIntAsync(db, "security.password_reset_minutes", 30, ct);
            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            await using (var ins = ds.CreateCommand("INSERT INTO zentriq.password_reset_tokens (user_id, token_hash, expires_at, requested_ip) VALUES (@u, @h, now() + make_interval(mins => @m), @ip)"))
            {
                ins.Parameters.AddWithValue("u", user.Id);
                ins.Parameters.AddWithValue("h", SHA256.HashData(Encoding.UTF8.GetBytes(token)));
                ins.Parameters.AddWithValue("m", minutes);
                ins.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Text) { Value = (object?)ip ?? DBNull.Value });
                await ins.ExecuteNonQueryAsync(ct);
            }
            var link = $"{ConsoleUrl(smtp, options)}/reset-password?token={token}";
            var text = $"""
                Hello {user.DisplayName},

                Someone (hopefully you) asked to reset the password of your Zentriq account {user.Email}.
                Open this link within {minutes} minutes to choose a new password:

                {link}

                If you did not ask for this, ignore this e-mail; your password stays the same.
                Request made from {ip ?? "unknown address"} at {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC.
                """;
            try
            {
                await mail.SendAsync(smtp, user.Email, "Reset your Zentriq password", text, null, ct);
                await audit.WriteAsync(new AuditActor(user.Id, user.Email, ip), "auth.password_reset_requested", user.Email, "Success", ct: ct);
            }
            catch (MailException ex)
            {
                lf.CreateLogger("Zentriq.PasswordReset").LogError("Password reset e-mail to {User} failed: {Error}", user.Email, ex.Message);
                await audit.WriteAsync(new AuditActor(user.Id, user.Email, ip), "auth.password_reset_requested", user.Email, "Failure", new { reason = "mail", error = ex.Message }, ct: ct);
            }
            return generic;
        }).AllowAnonymous().RequireRateLimiting("password-reset");

        g.MapPost("/reset", async (ResetBody body, ZentriqDbContext db, NpgsqlDataSource ds, IPasswordHasher<User> hasher, IDataProtectionProvider dp,
            AuditWriter audit, HttpContext http, CancellationToken ct) =>
        {
            var ip = http.Connection.RemoteIpAddress?.ToString();
            var invalid = Results.Problem(statusCode: 400, title: "This reset link is invalid or has expired. Request a new one from the sign-in page.");
            if (string.IsNullOrWhiteSpace(body.Token) || body.Token.Length > 100) return invalid;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(body.Token.Trim()));
            Guid tokenId, userId;
            await using (var find = ds.CreateCommand("SELECT id, user_id FROM zentriq.password_reset_tokens WHERE token_hash = @h AND used_at IS NULL AND expires_at > now()"))
            {
                find.Parameters.AddWithValue("h", hash);
                await using var r = await find.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return invalid;
                tokenId = r.GetGuid(0); userId = r.GetGuid(1);
            }
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
            if (u is null || u.Status != "active") return invalid;
            var err = PasswordPolicy.Validate(body.NewPassword, u.Email);
            if (err is not null) return Validation.Problem("newPassword", err);
            if (u.MfaEnabled)
            {
                // E-mail access alone must not bypass the second factor.
                if (string.IsNullOrWhiteSpace(body.MfaCode)) return Results.Json(new { mfaRequired = true, title = "Enter the code from your authenticator app." }, statusCode: 400);
                var secret = dp.CreateProtector(MfaPurpose).Unprotect(Convert.FromBase64String(u.MfaSecretProtected!));
                if (!Totp.Verify(secret, body.MfaCode, DateTimeOffset.UtcNow)) return Validation.Problem("mfaCode", "The authenticator code is not valid.");
            }
            u.PasswordHash = hasher.HashPassword(u, body.NewPassword);
            u.PasswordChangedAt = DateTimeOffset.UtcNow; u.SecurityStamp = Guid.NewGuid(); u.UpdatedAt = DateTimeOffset.UtcNow;
            u.FailedAttempts = 0; u.LockedUntil = null;
            await db.SaveChangesAsync(ct);
            await using (var use = ds.CreateCommand("UPDATE zentriq.password_reset_tokens SET used_at = now() WHERE user_id = @u AND used_at IS NULL"))
            {
                use.Parameters.AddWithValue("u", userId);
                await use.ExecuteNonQueryAsync(ct);
            }
            await audit.WriteAsync(new AuditActor(u.Id, u.Email, ip), "auth.password_reset", u.Email, "Success", new { tokenId }, ct: ct);
            return Results.Ok(new { message = "Your password has been changed. Sign in with the new password." });
        }).AllowAnonymous().RequireRateLimiting("password-reset");

        // ---- administrator actions ------------------------------------------------------------------
        api.MapPost("/users/{id:guid}/mfa/reset", async (Guid id, ZentriqDbContext db, RequestContext rc, CancellationToken ct) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u is null) return Results.NotFound();
            u.MfaEnabled = false; u.MfaSecretProtected = null; u.SecurityStamp = Guid.NewGuid(); u.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await rc.AuditAsync("user.mfa_reset", u.Email);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.UsersManage).WithTags("Users");

        var s = api.MapGroup("/system/smtp").WithTags("System");
        s.MapGet("", async (MailService mail, CancellationToken ct) =>
        {
            var v = await mail.LoadAsync(ct);
            return Results.Ok(new
            {
                v.Enabled, v.Host, v.Port, v.Security, v.Username, hasPassword = !string.IsNullOrEmpty(v.PasswordProtected), v.FromAddress, v.FromName,
                consoleUrl = v.ConsoleUrl, defaultConsoleUrl = DefaultConsoleUrl(options),
            });
        }).RequireAuthorization(Permissions.SettingsManage);

        s.MapPut("", async (SmtpBody body, MailService mail, RequestContext rc, CancellationToken ct) =>
        {
            var cur = await mail.LoadAsync(ct);
            if (body.Enabled)
            {
                if (string.IsNullOrWhiteSpace(body.Host)) return Validation.Problem("host", "Enter the mail server host name.");
                if (string.IsNullOrWhiteSpace(body.FromAddress) || !MailAddressOk(body.FromAddress)) return Validation.Problem("fromAddress", "Enter the sender e-mail address.");
            }
            if (body.Port is { } p && (p < 1 || p > 65535)) return Validation.Problem("port", "Port must be 1-65535.");
            if (body.Security is not (null or "starttls" or "none")) return Validation.Problem("security", "Choose STARTTLS or None.");
            if (!string.IsNullOrWhiteSpace(body.ConsoleUrl) && (!Uri.TryCreate(body.ConsoleUrl.Trim(), UriKind.Absolute, out var cu) || cu.Scheme != "https"))
                return Validation.Problem("consoleUrl", "Enter the console address starting with https://");
            var next = new SmtpSettings
            {
                Enabled = body.Enabled, Host = body.Host?.Trim(), Port = body.Port ?? 587, Security = body.Security ?? "starttls",
                Username = string.IsNullOrWhiteSpace(body.Username) ? null : body.Username.Trim(),
                PasswordProtected = body.ClearPassword == true ? null : !string.IsNullOrEmpty(body.Password) ? mail.ProtectPassword(body.Password) : cur.PasswordProtected,
                FromAddress = body.FromAddress?.Trim(), FromName = body.FromName?.Trim(), ConsoleUrl = string.IsNullOrWhiteSpace(body.ConsoleUrl) ? null : body.ConsoleUrl.Trim().TrimEnd('/'),
            };
            await mail.SaveAsync(next, rc.UserId, ct);
            await rc.AuditAsync("settings.smtp_update", next.Host, detail: new { next.Enabled, next.Host, next.Port, next.Security, next.Username, next.FromAddress, passwordChanged = !string.IsNullOrEmpty(body.Password) });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SettingsManage);

        s.MapPost("/test", async (SmtpTestBody body, MailService mail, RequestContext rc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.To) || !MailAddressOk(body.To)) return Validation.Problem("to", "Enter an e-mail address.");
            var smtp = await mail.LoadAsync(ct);
            try
            {
                await mail.SendAsync(smtp, body.To.Trim(), "Zentriq test e-mail", $"This is a test e-mail from Zentriq ({ConsoleUrl(smtp, options)}). E-mail delivery works.", null, ct);
            }
            catch (MailException ex) { return Results.Ok(new { ok = false, message = ex.Message }); }
            await rc.AuditAsync("settings.smtp_test", body.To);
            return Results.Ok(new { ok = true, message = $"Test e-mail sent to {body.To.Trim()}." });
        }).RequireAuthorization(Permissions.SettingsManage);
    }

    private static bool MailAddressOk(string s) => System.Net.Mail.MailAddress.TryCreate(s.Trim(), out _);

    private static string DefaultConsoleUrl(ZentriqOptions o) => $"https://{o.Https.HostName}{(o.Https.Port == 443 ? "" : $":{o.Https.Port}")}";
    private static string ConsoleUrl(SmtpSettings s, ZentriqOptions o) => string.IsNullOrWhiteSpace(s.ConsoleUrl) ? DefaultConsoleUrl(o) : s.ConsoleUrl.TrimEnd('/');

    private static async Task<int> SettingIntAsync(ZentriqDbContext db, string key, int fallback, CancellationToken ct)
    {
        var v = await db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return int.TryParse(v?.Trim().Trim('"'), out var n) && n is > 0 and <= 1440 ? n : fallback;
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>otpauth:// URI as an SVG QR code (scanned by Microsoft Authenticator, Google Authenticator and others).</summary>
    public static string QrSvg(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(6);
    }
}
