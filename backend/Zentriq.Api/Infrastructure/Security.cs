using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Api.Infrastructure;

public static class ZentriqClaims
{
    public const string UserId = "zq:uid";
    public const string Permission = "zq:perm";
    public const string Stamp = "zq:stamp";
    public const string Role = "zq:role";
    /// <summary>Signed in, but MFA is mandatory and not yet set up: no permissions until enrollment is confirmed.</summary>
    public const string MfaPending = "zq:mfa_pending";
    /// <summary>Changes when the user's roles or their permissions change: the session's permissions are then reloaded.</summary>
    public const string Access = "zq:access";
}

public static class SessionEvents
{
    /// <summary>
    /// API clients get 401/403 instead of redirects. Every request re-validates that the user still
    /// exists, is active and has the same security stamp (password changes revoke sessions); when the user's roles or their
    /// permissions changed (access stamp), the session's permissions are reloaded.
    /// </summary>
    public static CookieAuthenticationEvents Create() => new()
    {
        OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; },
        OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; },
        OnValidatePrincipal = async ctx =>
        {
            var uid = ctx.Principal?.FindFirstValue(ZentriqClaims.UserId);
            var stamp = ctx.Principal?.FindFirstValue(ZentriqClaims.Stamp);
            if (!Guid.TryParse(uid, out var id) || !Guid.TryParse(stamp, out var st)) { Reject(); return; }
            var db = ctx.HttpContext.RequestServices.GetRequiredService<ZentriqDbContext>();
            var u = await db.Users.AsNoTracking().Where(x => x.Id == id).FirstOrDefaultAsync();
            if (u is null || u.Status != "active" || u.SecurityStamp != st) { Reject(); return; }
            // Roles or role permissions changed since sign-in: reload them, the user stays signed in.
            if (ctx.Principal?.FindFirstValue(ZentriqClaims.Access) != u.AccessStamp.ToString())
            {
                var pending = ctx.Principal?.HasClaim(c => c.Type == ZentriqClaims.MfaPending) == true;
                ctx.ReplacePrincipal(await Zentriq.Api.Endpoints.AuthEndpoints.BuildPrincipalAsync(db, u, pending, ctx.HttpContext.RequestAborted));
                ctx.ShouldRenew = true;
            }

            void Reject()
            {
                ctx.RejectPrincipal();
                _ = ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        },
    };
}

/// <summary>
/// CSRF defence for the cookie-authenticated JSON API: state-changing requests must carry a custom
/// header. Browsers cannot add it cross-site without a CORS preflight, which this API never grants.
/// Combined with SameSite=Strict cookies.
/// </summary>
public sealed class CsrfHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Zentriq-Request";

    public async Task InvokeAsync(HttpContext ctx)
    {
        // Agent APIs use client certificates (no cookies), so they are not exposed to CSRF.
        if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/agent") && !HttpMethods.IsGet(ctx.Request.Method) &&
            !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method) &&
            ctx.Request.Headers[HeaderName] != "1")
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await Results.Problem(statusCode: 400, title: $"Missing {HeaderName} header.").ExecuteAsync(ctx);
            return;
        }
        await next(ctx);
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
            "font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
        return next(ctx);
    }
}

public static class ApiErrors
{
    public static (int Status, string Title) Map(Exception? ex) => ex switch
    {
        BadHttpRequestException b => (b.StatusCode, "The request is invalid."),
        DbUpdateConcurrencyException => (409, "The record was changed by someone else. Reload and try again."),
        Zentriq.Infrastructure.Licensing.WorkloadLimitExceededException w => (409, w.Message),
        _ => (500, "An unexpected error occurred. The details are in the server log."),
    };
}

public static class HttpsCertificate
{
    public static X509Certificate2 Load(ZentriqOptions options, ZentriqPaths paths)
    {
        if (!string.IsNullOrWhiteSpace(options.Https.CertificateThumbprint))
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, options.Https.CertificateThumbprint.Replace(" ", ""), validOnly: false);
            if (found.Count == 0) throw new InvalidOperationException($"HTTPS certificate {options.Https.CertificateThumbprint} was not found in LocalMachine\\My.");
            return found[0];
        }
        var path = options.Https.CertificatePath ?? paths.HttpsCertificateFile;
        var secrets = new SecretStore(SecretProtectorFactory.Create()).Read<AppSecrets>(paths.AppSecretsFile);
        return X509CertificateLoader.LoadPkcs12FromFile(path, secrets.HttpsPfxPassword, X509KeyStorageFlags.UserKeySet);
    }
}
