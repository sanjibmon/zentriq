using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zentriq.Infrastructure.Auth;

/// <summary>
/// Sign-in with Microsoft Entra ID (OpenID Connect): the authorization request (code flow with PKCE and a nonce), the
/// code exchange with the app's client secret, and the validation of the ID token (RS256 signature against the tenant's
/// published keys, issuer, audience, lifetime, nonce). Groups come from the token's groups claim, or — for people in
/// more than 200 groups — from Microsoft Graph with the app's own permission.
/// </summary>
public sealed class EntraOidc(HttpMessageHandler? handler = null, string loginBase = "https://login.microsoftonline.com", string graphBase = "https://graph.microsoft.com")
{
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, Dictionary<string, RSAParameters> Keys)> KeyCache = new();

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static bool ValidTenant(string? t) => Guid.TryParse(t, out _);

    public string AuthorizeUrl(EntraSignInSettings s, string redirectUri, string state, string nonce, string verifier, string? loginHint = null)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = s.ClientId, ["response_type"] = "code", ["redirect_uri"] = redirectUri, ["response_mode"] = "query",
            ["scope"] = "openid profile email", ["state"] = state, ["nonce"] = nonce, ["code_challenge"] = Challenge(verifier), ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        };
        if (!string.IsNullOrWhiteSpace(loginHint)) q["login_hint"] = loginHint;
        return $"{loginBase}/{Uri.EscapeDataString(s.TenantId)}/oauth2/v2.0/authorize?" + string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    private HttpClient Http()
    {
        var http = handler is null
            ? new HttpClient(new HttpClientHandler { UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials })
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zentriq", "1.1"));
        return http;
    }

    /// <summary>Exchanges the code and returns the person, validated.</summary>
    public async Task<DirectoryIdentity> SignInAsync(EntraSignInSettings s, string clientSecret, string code, string redirectUri, string verifier, string nonce, CancellationToken ct)
    {
        using var http = Http();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = s.ClientId, ["client_secret"] = clientSecret, ["grant_type"] = "authorization_code", ["code"] = code,
            ["redirect_uri"] = redirectUri, ["code_verifier"] = verifier, ["scope"] = "openid profile email",
        });
        using var resp = await Send(http, () => http.PostAsync($"{loginBase}/{Uri.EscapeDataString(s.TenantId)}/oauth2/v2.0/token", form, ct));
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = Parse(text);
        if (!resp.IsSuccessStatusCode)
        {
            var err = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : text;
            throw new DirectorySignInException("Microsoft did not accept the sign-in: " + Short(err), "token-refused");
        }
        var idToken = doc.RootElement.TryGetProperty("id_token", out var it) ? it.GetString() : null;
        if (string.IsNullOrEmpty(idToken)) throw new DirectorySignInException("Microsoft did not return an ID token.", "token-missing");
        var claims = await ValidateAsync(http, s, idToken, nonce, ct);
        return await IdentityAsync(http, s, clientSecret, claims, ct);
    }

    /// <summary>Checks the ID token and returns its claims.</summary>
    public async Task<JsonElement> ValidateAsync(HttpClient http, EntraSignInSettings s, string jwt, string nonce, CancellationToken ct)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw new DirectorySignInException("The ID token is not valid.", "token-invalid");
        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        var alg = header.RootElement.TryGetProperty("alg", out var a) ? a.GetString() : null;
        var kid = header.RootElement.TryGetProperty("kid", out var k) ? k.GetString() : null;
        if (alg != "RS256" || kid is null) throw new DirectorySignInException("The ID token is not signed as expected.", "token-invalid");
        var keys = await KeysAsync(http, s.TenantId, ct, refresh: false);
        if (!keys.TryGetValue(kid, out var key)) keys = await KeysAsync(http, s.TenantId, ct, refresh: true);
        if (!keys.TryGetValue(kid, out key)) throw new DirectorySignInException("The ID token's signing key is unknown.", "token-invalid");
        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(key);
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new DirectorySignInException("The ID token's signature is not valid.", "token-invalid");
        }
        var payload = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement.Clone();
        string? C(string n) => payload.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? N(string n) => payload.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.Equals(C("tid"), s.TenantId, StringComparison.OrdinalIgnoreCase)) throw new DirectorySignInException("The account is from another Microsoft Entra tenant.", "wrong-tenant");
        if (C("iss") != $"{loginBase}/{C("tid")}/v2.0") throw new DirectorySignInException("The ID token was not issued by your tenant.", "token-invalid");
        var audOk = payload.TryGetProperty("aud", out var aud) && (aud.ValueKind == JsonValueKind.String ? aud.GetString() == s.ClientId
            : aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(x => x.GetString() == s.ClientId));
        if (!audOk) throw new DirectorySignInException("The ID token is for another application.", "token-invalid");
        if (N("exp") is not { } exp || exp < now - 300) throw new DirectorySignInException("The ID token has expired.", "token-invalid");
        if (N("nbf") is { } nbf && nbf > now + 300) throw new DirectorySignInException("The ID token is not valid yet.", "token-invalid");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(C("nonce") ?? ""), Encoding.UTF8.GetBytes(nonce)))
            throw new DirectorySignInException("The sign-in was not started by this browser.", "nonce");
        return payload;
    }

    private async Task<DirectoryIdentity> IdentityAsync(HttpClient http, EntraSignInSettings s, string clientSecret, JsonElement c, CancellationToken ct)
    {
        string? C(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var oid = C("oid") ?? throw new DirectorySignInException("The ID token has no user id (oid).", "token-invalid");
        var email = C("email") ?? C("preferred_username") ?? C("upn") ?? throw new DirectorySignInException("The ID token has no e-mail address or user name.", "token-invalid");
        var groups = new List<(string, string?)>();
        if (c.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Array)
            foreach (var x in g.EnumerateArray()) if (x.GetString() is { } id) groups.Add((id, null));
        if (c.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
            foreach (var x in roles.EnumerateArray()) if (x.GetString() is { } r) groups.Add((r, null));
        var overage = c.TryGetProperty("_claim_names", out var cn) && cn.ValueKind == JsonValueKind.Object && cn.TryGetProperty("groups", out _)
                      || c.TryGetProperty("hasgroups", out var hg) && hg.ValueKind == JsonValueKind.True;
        if (overage) groups.AddRange((await GroupsFromGraphAsync(http, s, clientSecret, oid, ct)).Select(id => (id, (string?)null)));
        return new DirectoryIdentity("entra", oid.ToLowerInvariant(), email, C("name") ?? email, groups);
    }

    /// <summary>The groups of a person in many groups (application permission GroupMember.Read.All needed).</summary>
    private async Task<List<string>> GroupsFromGraphAsync(HttpClient http, EntraSignInSettings s, string clientSecret, string oid, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = s.ClientId, ["client_secret"] = clientSecret, ["grant_type"] = "client_credentials", ["scope"] = $"{graphBase}/.default",
        });
        using var tr = await Send(http, () => http.PostAsync($"{loginBase}/{Uri.EscapeDataString(s.TenantId)}/oauth2/v2.0/token", form, ct));
        using var tdoc = Parse(await tr.Content.ReadAsStringAsync(ct));
        if (!tr.IsSuccessStatusCode || !tdoc.RootElement.TryGetProperty("access_token", out var at))
            throw new DirectorySignInException("You are in many groups; Zentriq needs the Microsoft Graph application permission GroupMember.Read.All to read them.", "groups-overage");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{graphBase}/v1.0/users/{Uri.EscapeDataString(oid)}/getMemberObjects")
        {
            Content = new StringContent("{\"securityEnabledOnly\":false}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", at.GetString());
        using var gr = await Send(http, () => http.SendAsync(req, ct));
        using var gdoc = Parse(await gr.Content.ReadAsStringAsync(ct));
        if (!gr.IsSuccessStatusCode || !gdoc.RootElement.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Array)
            throw new DirectorySignInException("You are in many groups; Zentriq needs the Microsoft Graph application permission GroupMember.Read.All to read them.", "groups-overage");
        return v.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList();
    }

    private async Task<Dictionary<string, RSAParameters>> KeysAsync(HttpClient http, string tenant, CancellationToken ct, bool refresh)
    {
        if (!refresh && KeyCache.TryGetValue(tenant, out var cached) && cached.At > DateTimeOffset.UtcNow.AddHours(-12)) return cached.Keys;
        using var resp = await Send(http, () => http.GetAsync($"{loginBase}/{Uri.EscapeDataString(tenant)}/discovery/v2.0/keys", ct));
        using var doc = Parse(await resp.Content.ReadAsStringAsync(ct));
        var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("keys", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var key in arr.EnumerateArray())
            {
                if (key.TryGetProperty("kty", out var kty) && kty.GetString() == "RSA" && key.TryGetProperty("kid", out var kid) &&
                    key.TryGetProperty("n", out var n) && key.TryGetProperty("e", out var e) && kid.GetString() is { } id)
                    keys[id] = new RSAParameters { Modulus = FromBase64Url(n.GetString()!), Exponent = FromBase64Url(e.GetString()!) };
            }
        if (keys.Count == 0) throw new DirectorySignInException("The tenant's signing keys could not be read.", "keys");
        KeyCache[tenant] = (DateTimeOffset.UtcNow, keys);
        return keys;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient http, Func<Task<HttpResponseMessage>> f)
    {
        try { return await f(); }
        catch (HttpRequestException ex) { throw new DirectorySignInException("Microsoft Entra ID cannot be reached from the Zentriq server: " + ex.Message, "unreachable"); }
        catch (TaskCanceledException) { throw new DirectorySignInException("Microsoft Entra ID did not answer in time.", "unreachable"); }
    }

    private static JsonDocument Parse(string text)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }

    private static string Short(string? s) => s is null ? "" : s.Length > 300 ? s[..300] : s;

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
