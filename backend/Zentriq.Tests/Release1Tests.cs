using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Auth;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Lifecycle.Warranty;
using Zentriq.Infrastructure.Remote;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Tests;

/// <summary>A fake HTTP server: answers each request with (status, JSON body).</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, string?, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Seen.Add((request, body));
        var (status, text) = answer(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

/// <summary>Release 1 (build 113): warranty lookup, session recording, directory sign-in — no database.</summary>
public sealed class Release1Tests
{
    internal const string DellAnswer = """
        [{"id":1,"serviceTag":"ABC1234","shipDate":"2023-02-01T06:00:00Z","productLineDescription":"LATITUDE 5430","invalid":false,
          "entitlements":[{"serviceLevelDescription":"ProSupport","serviceLevelCode":"PS","startDate":"2023-02-01T06:00:00Z","endDate":"2026-02-02T05:59:59.000Z","entitlementType":"INITIAL"},
                          {"serviceLevelDescription":"Dell Digital Delivery","serviceLevelCode":"D","startDate":"2023-02-01T06:00:00Z","endDate":"2030-01-01T05:59:59Z","entitlementType":"INITIAL"}]},
         {"id":2,"serviceTag":"MAN0001","shipDate":"2022-01-10T06:00:00Z","productLineDescription":"OPTIPLEX 7090","invalid":false,
          "entitlements":[{"serviceLevelDescription":"Basic Hardware Service","serviceLevelCode":"ND","startDate":"2022-01-10T06:00:00Z","endDate":"2027-01-11T05:59:59Z","entitlementType":"EXTENDED"}]},
         {"id":3,"serviceTag":"ZZZ9999","invalid":true,"entitlements":[]}]
        """;

    internal static FakeHttp Vendors() => new((req, body) =>
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/oauth", StringComparison.Ordinal)) return (HttpStatusCode.OK, "{\"access_token\":\"t0k\",\"expires_in\":3600}");
        if (url.Contains("asset-entitlements", StringComparison.Ordinal))
        {
            // Only the service tags asked for.
            var tags = Uri.UnescapeDataString(url[(url.IndexOf("servicetags=", StringComparison.Ordinal) + 12)..]).Split(',');
            var all = JsonNode.Parse(DellAnswer)!.AsArray();
            return (HttpStatusCode.OK, new JsonArray(all.Where(n => tags.Contains(n!["serviceTag"]!.GetValue<string>())).Select(n => n!.DeepClone()).ToArray()).ToJsonString());
        }
        if (url.Contains("supportapi.lenovo.com", StringComparison.Ordinal))
            return (HttpStatusCode.OK, """
                [{"Serial":"PF2ABCDE","Product":"20XW-ThinkPad X1 Carbon Gen 9","InWarranty":true,"Purchased":"2022-05-01T00:00:00Z","Shipped":"2022-04-20T00:00:00Z",
                  "Warranty":[{"ID":"1EZ","Name":"Base Warranty","Type":"BASE","Start":"2022-04-20T00:00:00Z","End":"2025-05-19T00:00:00Z"},
                              {"ID":"5WS","Name":"Premier Support","Type":"UPGRADE","Start":"2022-04-20T00:00:00Z","End":"2027-05-19T00:00:00Z"}]}]
                """);
        if (url.Contains("productwarranty", StringComparison.Ordinal))
            return (HttpStatusCode.OK, """
                [{"serialNumber":"5CG1234XYZ","productNumber":"2Z6A6EA","productDescription":"HP EliteBook 840 G8",
                  "offers":[{"offerDescription":"HP Hardware Maintenance Onsite Support","serviceObligationLineItemStartDate":"2021-06-01","serviceObligationLineItemEndDate":"2024-06-01"},
                            {"offerDescription":"HP Care Pack Next Business Day","serviceObligationLineItemStartDate":"2024-06-02","serviceObligationLineItemEndDate":"2026-06-01"}]}]
                """);
        return (HttpStatusCode.NotFound, "{}");
    });

    [Fact]
    public void Vendors_serials_and_dates_are_recognised()
    {
        Assert.Equal("dell", WarrantyClients.Detect("Dell Inc."));
        Assert.Equal("lenovo", WarrantyClients.Detect("LENOVO"));
        Assert.Equal("hp", WarrantyClients.Detect("HP"));
        Assert.Equal("hp", WarrantyClients.Detect("Hewlett-Packard"));
        Assert.Null(WarrantyClients.Detect("Hewlett Packard Enterprise"));
        Assert.Null(WarrantyClients.Detect("HPE"));
        Assert.Null(WarrantyClients.Detect("Microsoft Corporation"));
        Assert.Equal("5CG1234XYZ", WarrantyClients.CleanSerial(" 5cg-1234xyz "));
        Assert.Null(WarrantyClients.CleanSerial("To be filled by O.E.M."));
        Assert.Null(WarrantyClients.CleanSerial("0000000"));
        Assert.Null(WarrantyClients.CleanSerial("Default string"));
        Assert.Equal("2Z6A6EA", WarrantyClients.HpProductNumber("2Z6A6EA#ABU"));
        Assert.Equal(new DateOnly(2027, 3, 14), WarrantyClients.ParseDay("2027-03-14T05:59:59.000Z"));
        Assert.Equal(new DateOnly(2025, 1, 31), WarrantyClients.ParseDay("2025-01-31"));
        Assert.Null(WarrantyClients.ParseDay("soon"));
    }

    [Fact]
    public async Task Dell_Lenovo_and_HP_answers_are_read()
    {
        var http = Vendors();
        using var client = new HttpClient(http);
        var dell = await WarrantyClients.LookupAsync(client, new WarrantyProviderConfig("dell", "cid", "csec", null), [("ABC1234", null), ("ZZZ9999", null)], default);
        Assert.Equal(new DateOnly(2026, 2, 2), dell["ABC1234"].End);          // Dell Digital Delivery is not hardware cover
        Assert.Equal("ProSupport", dell["ABC1234"].Latest!.Name);
        Assert.Equal("LATITUDE 5430", dell["ABC1234"].Product);
        Assert.False(dell["ZZZ9999"].Found);
        var token = http.Seen.First(s => s.Request.RequestUri!.ToString().Contains("/oauth"));
        Assert.Equal("Basic", token.Request.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("cid:csec")), token.Request.Headers.Authorization.Parameter);
        Assert.Contains("grant_type=client_credentials", token.Body);
        Assert.Equal("t0k", http.Seen.First(s => s.Request.RequestUri!.ToString().Contains("asset-entitlements")).Request.Headers.Authorization!.Parameter);

        var lenovo = await WarrantyClients.LookupAsync(client, new WarrantyProviderConfig("lenovo", "lenovo-id", null, null), [("PF2ABCDE", null)], default);
        Assert.Equal(new DateOnly(2027, 5, 19), lenovo["PF2ABCDE"].End);
        Assert.Equal(new DateOnly(2022, 4, 20), lenovo["PF2ABCDE"].Start);
        Assert.Equal("lenovo-id", http.Seen.Last().Request.Headers.GetValues("ClientID").Single());

        var hp = await WarrantyClients.LookupAsync(client, new WarrantyProviderConfig("hp", "hid", "hsec", null), [("5CG1234XYZ", "2Z6A6EA#ABU")], default);
        Assert.Equal(new DateOnly(2026, 6, 1), hp["5CG1234XYZ"].End);
        Assert.Equal("HP Care Pack Next Business Day", hp["5CG1234XYZ"].Latest!.Name);
        Assert.Contains("\"pn\":\"2Z6A6EA\"", http.Seen.Last().Body);

        // A refused key is explained.
        using var refused = new HttpClient(new FakeHttp((_, _) => (HttpStatusCode.Unauthorized, "{}")));
        var ex = await Assert.ThrowsAsync<WarrantyApiException>(() => WarrantyClients.LookupAsync(refused, new WarrantyProviderConfig("dell", "x", "y", null), [("ABC1234", null)], default));
        Assert.Contains("refused the key", ex.Message);
    }

    [Fact]
    public void Recording_files_are_read_and_the_engine_is_configured()
    {
        var uid = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var bytes = RemoteRecordings.Build(new { magic = "MeshCentralRelaySession", ver = 1, userid = "user//" + RemoteSupportService.RecordedMeshUserName(uid), username = RemoteSupportService.RecordedMeshUserName(uid),
            nodeid = "node//abc", nodename = "PC-01", protocol = 2 },
            [(2, 1, t0.AddSeconds(1), new byte[] { 0, 7, 0, 8, 4, 0, 3, 0 }), (2, 2, t0.AddSeconds(2), Encoding.UTF8.GetBytes("key")), (3, 0, t0.AddSeconds(65), Encoding.UTF8.GetBytes("MeshCentralMCREC"))]);
        var info = RemoteRecordings.Read(new MemoryStream(bytes))!;
        Assert.True(info.Complete);
        Assert.Equal(2, info.Protocol);
        Assert.Equal("node//abc", info.NodeId);
        Assert.Equal(t0.AddSeconds(65).ToUnixTimeMilliseconds(), info.End!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(uid, RemoteSupportService.UserOfMeshUser(info.MeshUser));
        Assert.Equal(uid, RemoteSupportService.UserOfMeshUser("user//" + RemoteSupportService.MeshUserName(uid)));
        Assert.Null(RemoteRecordings.Read(new MemoryStream(Encoding.UTF8.GetBytes("not a recording at all"))));
        Assert.Equal("desktop", RemoteRecordings.Kind(2));

        Assert.Null(RemoteRecordings.PathOf(Path.GetTempPath(), "../evil.mcrec"));
        Assert.Null(RemoteRecordings.PathOf(Path.GetTempPath(), "a.txt"));
        Assert.NotNull(RemoteRecordings.PathOf(Path.GetTempPath(), "sub/a.mcrec"));

        var domain = new JsonObject();
        Assert.True(RemoteServerConfig.ApplyRecording(domain, true, @"C:\ProgramData\Zentriq\Remote\recordings"));
        Assert.False(RemoteServerConfig.ApplyRecording(domain, true, @"C:\ProgramData\Zentriq\Remote\recordings"));
        Assert.True(domain["sessionRecording"]!["onlySelectedUsers"]!.GetValue<bool>());
        Assert.True(RemoteServerConfig.ApplyRecording(domain, false, "x"));
        Assert.Null(domain["sessionRecording"]);
    }

    [Fact]
    public void Ldap_names_and_filters_are_safe()
    {
        Assert.Equal(@"a\2ab\28c\29\5c", LdapSignIn.Escape(@"a*b(c)\"));
        Assert.Equal((@"CONTOSO\jane", "jane", (string?)null), LdapSignIn.Names(@"CONTOSO\jane", null));
        Assert.Equal(("jane@contoso.com", "jane", (string?)"jane@contoso.com"), LdapSignIn.Names("jane@contoso.com", "CONTOSO"));
        Assert.Equal((@"CONTOSO\jane", "jane", (string?)null), LdapSignIn.Names("jane", "CONTOSO"));
        var s = new LdapSignInSettings { Enabled = true, Host = "dc01.contoso.com" };
        Assert.Equal("empty", Assert.Throws<DirectorySignInException>(() => LdapSignIn.Authenticate(s, "jane", "")).Reason);   // never an anonymous bind
        Assert.Equal("bad-name", Assert.Throws<DirectorySignInException>(() => LdapSignIn.Authenticate(s, "CN=Jane,DC=contoso,DC=com", "x")).Reason);
    }

    private static string B64(byte[] b) => EntraOidc.Base64Url(b);

    internal static (FakeHttp Http, Func<object, string> Sign) Entra(string tenant)
    {
        var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        string Sign(object claims)
        {
            var head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"kid\":\"k1\",\"typ\":\"JWT\"}"));
            var body = B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)));
            return $"{head}.{body}.{B64(rsa.SignData(Encoding.ASCII.GetBytes(head + "." + body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
        }
        string? next = null;
        var http = new FakeHttp((req, body) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/discovery/v2.0/keys", StringComparison.Ordinal))
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "k1", use = "sig", n = B64(p.Modulus!), e = B64(p.Exponent!) } } }));
            if (url.EndsWith("/oauth2/v2.0/token", StringComparison.Ordinal) && body!.Contains("authorization_code"))
                return body.Contains("code=bad") ? (HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS70008: expired\"}") : (HttpStatusCode.OK, JsonSerializer.Serialize(new { id_token = next }));
            return (HttpStatusCode.NotFound, "{}");
        });
        return (http, claims => next = Sign(claims));
    }

    [Fact]
    public async Task Microsoft_sign_in_validates_the_id_token()
    {
        var tenant = Guid.NewGuid().ToString();
        var client = Guid.NewGuid().ToString();
        var (http, issue) = Entra(tenant);
        var oidc = new EntraOidc(http, "https://login.test", "https://graph.test");
        var s = new EntraSignInSettings { Enabled = true, TenantId = tenant, ClientId = client };
        var url = oidc.AuthorizeUrl(s, "https://zq.contoso.com/api/v1/auth/oidc/callback", "st", "n1", "verifier-verifier");
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("code_challenge=" + EntraOidc.Challenge("verifier-verifier"), url);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        object Claims(string nonce = "n1", string? tid = null, string? aud = null) => new Dictionary<string, object>
        {
            ["iss"] = $"https://login.test/{tid ?? tenant}/v2.0", ["tid"] = tid ?? tenant, ["aud"] = aud ?? client, ["exp"] = now + 3600, ["nbf"] = now - 10, ["nonce"] = nonce,
            ["oid"] = "AAAA-1111", ["preferred_username"] = "jane@contoso.com", ["name"] = "Jane Doe", ["groups"] = new[] { "g-admins" }, ["roles"] = new[] { "Zentriq.Viewer" },
        };
        issue(Claims());
        var who = await oidc.SignInAsync(s, "app-key", "c0de", "https://zq/cb", "v", "n1", default);
        Assert.Equal("aaaa-1111", who.ExternalId);
        Assert.Equal("jane@contoso.com", who.Email);
        Assert.Contains(who.Groups, g => g.Ref == "g-admins");
        Assert.Contains(who.Groups, g => g.Ref == "Zentriq.Viewer");
        Assert.Contains("code_verifier=v", http.Seen.Last(x => x.Request.RequestUri!.ToString().EndsWith("/token")).Body);

        issue(Claims(nonce: "other"));
        Assert.Equal("nonce", (await Assert.ThrowsAsync<DirectorySignInException>(() => oidc.SignInAsync(s, "k", "c", "https://zq/cb", "v", "n1", default))).Reason);
        issue(Claims(tid: Guid.NewGuid().ToString()));
        Assert.Equal("wrong-tenant", (await Assert.ThrowsAsync<DirectorySignInException>(() => oidc.SignInAsync(s, "k", "c", "https://zq/cb", "v", "n1", default))).Reason);
        issue(Claims(aud: "someone-else"));
        Assert.Equal("token-invalid", (await Assert.ThrowsAsync<DirectorySignInException>(() => oidc.SignInAsync(s, "k", "c", "https://zq/cb", "v", "n1", default))).Reason);
        Assert.Equal("token-refused", (await Assert.ThrowsAsync<DirectorySignInException>(() => oidc.SignInAsync(s, "k", "bad", "https://zq/cb", "v", "n1", default))).Reason);

        // A token signed by another key is refused.
        var (_, otherIssuer) = Entra(tenant);
        var forged = otherIssuer(Claims());
        using var hc = new HttpClient(http);
        Assert.Equal("token-invalid", (await Assert.ThrowsAsync<DirectorySignInException>(() => oidc.ValidateAsync(hc, s, forged, "n1", default))).Reason);
    }
}

/// <summary>Release 1 with PostgreSQL: warranty lookup, recordings index, directory accounts and roles.</summary>
public sealed class Release1DbTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        if (Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG") is { Length: > 0 })
            await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.1.0-test");
    }
    public Task DisposeAsync() => _db.DisposeAsync();

    private static async Task<object?> Scalar(NpgsqlDataSource ds, string sql, params (string, object)[] ps)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<Guid> Device(NpgsqlDataSource ds, string name, string manufacturer, string serial) =>
        (Guid)(await Scalar(ds, "INSERT INTO zentriq.assets (hostname, category, status, os_family, manufacturer, serial_number) VALUES (@h, 'endpoint', 'active', 'windows', @m, @s) RETURNING id",
            ("h", name), ("m", manufacturer), ("s", serial)))!;

    [DbFact]
    public async Task Warranty_lookup_fills_the_lifecycle_record_and_keeps_manual_dates()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var keys = new AppSecrets { CredentialKey = SecretStore.NewKey() };
        var protector = new CredentialProtector(() => keys);
        var svc = new WarrantyService(ds, protector, Release1Tests.Vendors());
        var a1 = await Device(ds, "LAT-01", "Dell Inc.", "ABC1234");
        var a2 = await Device(ds, "OPT-02", "Dell Inc.", "MAN0001");
        var a3 = await Device(ds, "X1-03", "LENOVO", "PF2ABCDE");
        await Scalar(ds, "UPDATE zentriq.asset_lifecycle SET warranty_end = '2024-01-01', warranty_provider = 'Reseller', warranty_source = 'manual' WHERE asset_id = @a", ("a", a2));

        // No key yet: nothing is called.
        var none = await svc.LookupAsync([a1], "test", default);
        Assert.Equal(1, none.NoKey);
        Assert.Equal("no_key", await Scalar(ds, "SELECT status FROM zentriq.warranty_lookups WHERE asset_id = @a", ("a", a1)));

        await svc.SaveProviderAsync("dell", true, "cid", "csec", null, "test", default);
        Assert.True((await svc.ProvidersAsync(default)).Single(p => p.Vendor == "dell").Configured);
        Assert.Null(await Scalar(ds, "SELECT status FROM zentriq.warranty_lookups WHERE asset_id = @a", ("a", a1)));   // due again after a key is entered
        Assert.DoesNotContain("csec", (string)(await Scalar(ds, "SELECT secret_protected FROM zentriq.warranty_providers WHERE vendor = 'dell'"))!);

        var r = await svc.LookupAsync([a1, a2, a3], "test", default);
        Assert.Equal(2, r.Found);
        Assert.Equal(1, r.NoKey);                     // Lenovo has no key
        Assert.Equal(new DateTime(2026, 2, 2), await Scalar(ds, "SELECT warranty_end FROM zentriq.asset_lifecycle WHERE asset_id = @a", ("a", a1)));
        Assert.Equal("Dell", await Scalar(ds, "SELECT warranty_provider FROM zentriq.asset_lifecycle WHERE asset_id = @a", ("a", a1)));
        Assert.Equal("vendor", await Scalar(ds, "SELECT warranty_source FROM zentriq.asset_lifecycle WHERE asset_id = @a", ("a", a1)));
        // Entered by hand: kept, the answer is shown on the device.
        Assert.Equal(new DateTime(2024, 1, 1), await Scalar(ds, "SELECT warranty_end FROM zentriq.asset_lifecycle WHERE asset_id = @a", ("a", a2)));
        Assert.Equal(false, await Scalar(ds, "SELECT applied FROM zentriq.warranty_lookups WHERE asset_id = @a", ("a", a2)));
        Assert.Equal(new DateTime(2027, 1, 11), await Scalar(ds, "SELECT warranty_end FROM zentriq.warranty_lookups WHERE asset_id = @a", ("a", a2)));

        await svc.SaveSettingsAsync(new WarrantySettings { Auto = true, RefreshDays = 30, OverwriteManual = true }, "test", default);
        await svc.LookupAsync([a2], "test", default);
        Assert.Equal(new DateTime(2027, 1, 11), await Scalar(ds, "SELECT warranty_end FROM zentriq.asset_lifecycle WHERE asset_id = @a", ("a", a2)));

        // Background: the Dell devices were just checked; only the Lenovo one is due (and still has no key, so it is
        // skipped without an answer being saved).
        await Scalar(ds, "DELETE FROM zentriq.warranty_lookups WHERE asset_id = @a", ("a", a3));
        var due = await svc.RunDueAsync(100, default);
        Assert.Equal(0, due.Checked);
        Assert.Equal(1, due.NoKey);
        Assert.Null(await Scalar(ds, "SELECT status FROM zentriq.warranty_lookups WHERE asset_id = @a", ("a", a3)));
    }

    [DbFact]
    public async Task Recordings_are_indexed_linked_to_the_session_and_deleted_after_the_retention()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var uid = (Guid)(await Scalar(ds, "INSERT INTO zentriq.users (email, display_name, password_hash) VALUES ('tech@contoso.com', 'Tech One', 'AA==') RETURNING id"))!;
        var asset = await Device(ds, "PC-01", "Dell Inc.", "REC0001");
        await Scalar(ds, "INSERT INTO zentriq.remote_nodes (node_id, asset_id, name) VALUES ('node//abc', @a, 'PC-01')", ("a", asset));
        var session = (long)(await Scalar(ds, "INSERT INTO zentriq.remote_sessions (user_id, user_name, asset_id, node_id, device_name, view, created_at) VALUES (@u, 'Tech One', @a, 'node//abc', 'PC-01', 'desktop', now() - interval '1 minute') RETURNING id",
            ("u", uid), ("a", asset)))!;
        var dir = Path.Combine(Path.GetTempPath(), "zq-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            var t0 = DateTimeOffset.UtcNow.AddSeconds(-30);
            File.WriteAllBytes(Path.Combine(dir, "sub", "s1.mcrec"), RemoteRecordings.Build(
                new { magic = "MeshCentralRelaySession", ver = 1, userid = "user//" + RemoteSupportService.RecordedMeshUserName(uid), username = RemoteSupportService.RecordedMeshUserName(uid), nodeid = "node//abc", nodename = "PC-01", protocol = 2 },
                [(2, 1, t0.AddSeconds(1), new byte[8]), (3, 0, t0.AddSeconds(20), Encoding.UTF8.GetBytes("MeshCentralMCREC"))]));
            // Still being written: no end block, changed just now.
            File.WriteAllBytes(Path.Combine(dir, "s2.mcrec"), RemoteRecordings.Build(new { magic = "MeshCentralRelaySession", protocol = 1, nodeid = "node//abc" }, [(2, 1, t0, new byte[4])]));

            var r = await RemoteRecordings.IndexAsync(ds, dir, 90, default);
            Assert.Equal(1, r.Added);
            Assert.Equal(session, await Scalar(ds, "SELECT session_id FROM zentriq.remote_recordings WHERE file_name = 'sub/s1.mcrec'"));
            Assert.Equal("Tech One", await Scalar(ds, "SELECT user_name FROM zentriq.remote_recordings WHERE file_name = 'sub/s1.mcrec'"));
            Assert.Equal(asset, await Scalar(ds, "SELECT asset_id FROM zentriq.remote_recordings WHERE file_name = 'sub/s1.mcrec'"));
            Assert.Equal(true, await Scalar(ds, "SELECT recorded FROM zentriq.remote_sessions WHERE id = @s", ("s", session)));
            Assert.Equal(0, (await RemoteRecordings.IndexAsync(ds, dir, 90, default)).Added);   // not twice

            await Scalar(ds, "UPDATE zentriq.remote_recordings SET started_at = now() - interval '5 days'");
            var del = await RemoteRecordings.IndexAsync(ds, dir, 3, default);
            Assert.Equal(1, del.Deleted);
            Assert.False(File.Exists(Path.Combine(dir, "sub", "s1.mcrec")));
            Assert.NotNull(await Scalar(ds, "SELECT deleted_at FROM zentriq.remote_recordings WHERE file_name = 'sub/s1.mcrec'"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [DbFact]
    public async Task Directory_accounts_get_the_roles_of_their_mapped_groups()
    {
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        const string admins = "CN=Zentriq Admins,OU=Groups,DC=contoso,DC=com";
        await Scalar(ds, "INSERT INTO zentriq.auth_group_mappings (provider, group_ref, group_name, role_id) VALUES ('ldap', @g, 'Zentriq Admins', 'it_assets')", ("g", admins));
        var who = new DirectoryIdentity("ldap", "11111111-2222-3333-4444-555555555555", "jane@contoso.com", "Jane Doe", [(admins.ToLowerInvariant(), "Zentriq Admins"), ("CN=Other,DC=contoso,DC=com", null)]);

        var first = await DirectoryUsers.ResolveAsync(ds, who, createUsers: true, default);
        Assert.True(first.Created);
        Assert.Equal(new[] { "it_assets" }, first.Roles);
        Assert.Equal("ldap", await Scalar(ds, "SELECT auth_source FROM zentriq.users WHERE id = @u", ("u", first.UserId)));
        Assert.Equal(1L, await Scalar(ds, "SELECT count(*) FROM zentriq.user_role_grants WHERE user_id = @u AND role_id = 'it_assets'", ("u", first.UserId)));
        var again = await DirectoryUsers.ResolveAsync(ds, who with { DisplayName = "Jane Smith" }, createUsers: true, default);
        Assert.False(again.Created);
        Assert.Equal(first.UserId, again.UserId);
        Assert.Equal("Jane Smith", await Scalar(ds, "SELECT display_name FROM zentriq.users WHERE id = @u", ("u", first.UserId)));

        // Left the group: the role is taken away and the sign-in refused.
        var stamp = await Scalar(ds, "SELECT access_stamp FROM zentriq.users WHERE id = @u", ("u", first.UserId));
        var left = await Assert.ThrowsAsync<DirectorySignInException>(() => DirectoryUsers.ResolveAsync(ds, who with { Groups = [] }, true, default));
        Assert.Equal("no-group", left.Reason);
        Assert.Equal(0L, await Scalar(ds, "SELECT count(*) FROM zentriq.user_role_grants WHERE user_id = @u", ("u", first.UserId)));
        Assert.NotEqual(stamp, await Scalar(ds, "SELECT access_stamp FROM zentriq.users WHERE id = @u", ("u", first.UserId)));

        // Nobody in a mapped group is created; a local account with the same e-mail is not taken over.
        Assert.Equal("no-group", (await Assert.ThrowsAsync<DirectorySignInException>(() => DirectoryUsers.ResolveAsync(ds,
            new DirectoryIdentity("entra", "oid-1", "bob@contoso.com", "Bob", [("g1", null)]), true, default))).Reason);
        Assert.Equal(0L, await Scalar(ds, "SELECT count(*) FROM zentriq.users WHERE email = 'bob@contoso.com'"));
        await Scalar(ds, "INSERT INTO zentriq.users (email, display_name, password_hash) VALUES ('admin@contoso.com', 'Admin', 'AA==')");
        await Scalar(ds, "INSERT INTO zentriq.auth_group_mappings (provider, group_ref, role_id) VALUES ('entra', 'g1', 'admin')");
        Assert.Equal("email-in-use", (await Assert.ThrowsAsync<DirectorySignInException>(() => DirectoryUsers.ResolveAsync(ds,
            new DirectoryIdentity("entra", "oid-2", "admin@contoso.com", "Mallory", [("g1", null)]), true, default))).Reason);
        Assert.Equal("no-account", (await Assert.ThrowsAsync<DirectorySignInException>(() => DirectoryUsers.ResolveAsync(ds,
            new DirectoryIdentity("entra", "oid-3", "new@contoso.com", "New", [("g1", null)]), createUsers: false, default))).Reason);
        var bob = await DirectoryUsers.ResolveAsync(ds, new DirectoryIdentity("entra", "oid-1", "bob@contoso.com", "Bob", [("G1", null)]), true, default);
        Assert.Equal(new[] { "admin" }, bob.Roles);   // group ids compare without case
    }
}
