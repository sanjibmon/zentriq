using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Npgsql;
using Zentriq.Infrastructure.Integrations;

namespace Zentriq.Api.Infrastructure;

/// <summary>
/// Read-only API tokens (Authorization: Bearer zq_…). Only the SHA-256 hash is stored. A token's permissions are the
/// ones chosen when it was created, limited to what its creator still has; it stops working when revoked, expired or
/// when the creator is disabled. Only GET/HEAD requests are accepted.
/// </summary>
public sealed class ApiTokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, NpgsqlDataSource ds)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "ApiToken";
    public const string TokenIdClaim = "zq:token";

    public static bool HasToken(HttpRequest request) =>
        request.Headers.Authorization.ToString() is { } h && h.StartsWith("Bearer " + IntegrationStore.TokenPrefix, StringComparison.Ordinal);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!HasToken(Request)) return AuthenticateResult.NoResult();
        if (!HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method))
            return AuthenticateResult.Fail("API tokens are read-only (GET requests only).");
        var token = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        if (token.Length is < 20 or > 200) return AuthenticateResult.Fail("Invalid token.");

        await using var cmd = ds.CreateCommand("""
            SELECT t.id, t.name, u.id, u.email, u.display_name,
                   ARRAY(SELECT DISTINCT rp.permission_code FROM zentriq.user_role_grants ur JOIN zentriq.role_permissions rp ON rp.role_id = ur.role_id
                          WHERE ur.user_id = u.id AND rp.permission_code = ANY(t.permissions)),
                   t.last_used_at IS NULL OR t.last_used_at < now() - interval '1 minute'
              FROM zentriq.api_tokens t JOIN zentriq.users u ON u.id = t.created_by
             WHERE t.token_hash = @h AND t.revoked_at IS NULL AND (t.expires_at IS NULL OR t.expires_at > now()) AND u.status = 'active'
            """);
        cmd.Parameters.AddWithValue("h", IntegrationStore.HashToken(token));
        Guid tokenId, userId; string name, email, display; string[] perms; bool touch;
        await using (var r = await cmd.ExecuteReaderAsync(Context.RequestAborted))
        {
            if (!await r.ReadAsync(Context.RequestAborted)) return AuthenticateResult.Fail("The API token is not valid, has expired or was revoked.");
            tokenId = r.GetGuid(0); name = r.GetString(1); userId = r.GetGuid(2); email = r.GetString(3); display = r.GetString(4);
            perms = r.GetFieldValue<string[]>(5); touch = r.GetBoolean(6);
        }
        if (touch)
        {
            await using var up = ds.CreateCommand("UPDATE zentriq.api_tokens SET last_used_at = now(), last_used_ip = @ip WHERE id = @id");
            up.Parameters.AddWithValue("id", tokenId);
            up.Parameters.Add(new NpgsqlParameter("ip", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)Context.Connection.RemoteIpAddress?.ToString() ?? DBNull.Value });
            await up.ExecuteNonQueryAsync(Context.RequestAborted);
        }
        var claims = new List<Claim>
        {
            new(ZentriqClaims.UserId, userId.ToString()),
            new(ClaimTypes.Email, $"{email} (API token '{name}')"),
            new(ClaimTypes.Name, display),
            new(TokenIdClaim, tokenId.ToString()),
        };
        claims.AddRange(perms.Select(p => new Claim(ZentriqClaims.Permission, p)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}

public static class AuthSchemes
{
    /// <summary>Picks the API-token scheme when a Zentriq bearer token is sent, otherwise the session cookie.</summary>
    public const string Smart = "Zentriq";

    public static string Select(HttpContext ctx) => ApiTokenAuthenticationHandler.HasToken(ctx.Request) ? ApiTokenAuthenticationHandler.Scheme : CookieAuthenticationDefaults.AuthenticationScheme;
}
