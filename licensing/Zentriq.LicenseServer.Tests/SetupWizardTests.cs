using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zentriq.LicenseServer.Services;

namespace Zentriq.LicenseServer.Tests;

/// <summary>
/// An installed server (<c>--data</c>) before and after the setup wizard: only the wizard until it is finished,
/// owner first, key created or imported (never replaced), backup confirmation, address settings, automatic unlock
/// at the next start.
/// </summary>
public sealed class SetupWizardTests : IAsyncLifetime
{
    private const string Phrase = "setup wizard signing passphrase";
    private const string OwnerPass = "setup owner passphrase 1";
    private readonly string _db = "zls_setup_" + Guid.NewGuid().ToString("N")[..12];
    private string _cs = "";
    private string _dataDir = "";
    private WebApplication? _app;
    private HttpClient _client = null!;
    private string? _cookie;

    /// <summary>TestServer connections have no address; the wizard answers loopback only.</summary>
    private sealed class Loopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress ??= IPAddress.Loopback; return n(ctx); });
            next(app);
        };
    }

    public async Task InitializeAsync()
    {
        var pg = Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return;
        await using (var c = new NpgsqlConnection(pg))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_db}", c);
            await cmd.ExecuteNonQueryAsync();
        }
        _cs = new NpgsqlConnectionStringBuilder(pg) { Database = _db }.ConnectionString;
        _dataDir = Directory.CreateTempSubdirectory("zlsdata").FullName;
        await StartAsync();
    }

    private async Task StartAsync()
    {
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        _app = Program.Build(["--data", _dataDir, $"--ConnectionStrings:LicenseServer={_cs}"], b =>
        {
            b.WebHost.UseTestServer();
            b.Services.AddSingleton<IStartupFilter, Loopback>();
        });
        await MigrationRunner.MigrateAsync(_app.Services.GetRequiredService<NpgsqlDataSource>());
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _cookie = null;
    }

    public async Task DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG"));
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_db} WITH (FORCE)", c);
        await cmd.ExecuteNonQueryAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod m, string path, object? body = null, bool csrf = true)
    {
        var req = new HttpRequestMessage(m, path);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (csrf) req.Headers.Add("X-ZLS-Request", "1");
        if (_cookie is not null) req.Headers.Add("Cookie", _cookie);
        var resp = await _client.SendAsync(req);
        if (resp.Headers.TryGetValues("Set-Cookie", out var sc)) _cookie = sc.First().Split(';')[0];
        return resp;
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<JsonElement> State() => SendAsync(HttpMethod.Get, "/setup/api/state").ContinueWith(t => Ok(t.Result)).Unwrap();

    private static string SelfSignedPfx(string host, string password)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return Convert.ToBase64String(cert.Export(X509ContentType.Pfx, password));
    }

    [DbFact]
    public async Task Wizard_owner_key_backup_access_finish_and_automatic_unlock_after_restart()
    {
        var s = await State();
        Assert.False(s.GetProperty("ownerExists").GetBoolean());
        Assert.False(s.GetProperty("complete").GetBoolean());

        // Until the setup is finished the portal leads to the wizard.
        var portal = await SendAsync(HttpMethod.Get, "/admin/");
        Assert.Equal(HttpStatusCode.Redirect, portal.StatusCode);
        Assert.Equal("/setup/", portal.Headers.Location?.ToString());

        // Owner first (request header required), then signed in.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/setup/api/owner", new { email = "owner@zentriq.test", password = OwnerPass }, csrf: false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/setup/api/owner", new { email = "owner@zentriq.test", password = "short" })).StatusCode);
        await Ok(await SendAsync(HttpMethod.Post, "/setup/api/owner", new { email = "owner@zentriq.test", name = "Owner", password = OwnerPass }));
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/setup/api/owner", new { email = "x@zentriq.test", password = OwnerPass })).StatusCode);
        Assert.True((await State()).GetProperty("signedIn").GetBoolean());

        // No key yet: licensing answers "not set up".
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendAsync(HttpMethod.Get, "/admin/api/catalog")).StatusCode);

        // Key ceremony.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/setup/api/key/new", new { keyId = "zentriq-prod-2026-11", passphrase = "too short" })).StatusCode);
        var key = await Ok(await SendAsync(HttpMethod.Post, "/setup/api/key/new", new { keyId = "zentriq-prod-2026-11", passphrase = Phrase }));
        var fp = key.GetProperty("fingerprint").GetString()!;
        Assert.Matches("^SHA256:([0-9A-F]{2}:){31}[0-9A-F]{2}$", fp);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/setup/api/key/new", new { passphrase = Phrase + "x" })).StatusCode);
        var entry = await (await SendAsync(HttpMethod.Get, "/setup/api/key/product-entry")).Content.ReadAsStringAsync();
        Assert.Contains("zentriq-prod-2026-11", entry);
        Assert.DoesNotContain("PRIVATE", entry);
        var backup = await (await SendAsync(HttpMethod.Get, "/setup/api/key/backup")).Content.ReadAsStringAsync();
        Assert.Contains("BEGIN ENCRYPTED PRIVATE KEY", backup);
        var catalog = await Ok(await SendAsync(HttpMethod.Get, "/admin/api/catalog"));
        Assert.Equal(fp, catalog.GetProperty("signing").GetProperty("fingerprint").GetString());

        // Finishing needs the backup confirmed.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/setup/api/finish", new { })).StatusCode);
        await SendAsync(HttpMethod.Post, "/setup/api/key/backed-up", new { });

        // Address settings: validated; a certificate must match the name.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/setup/api/access", new { mode = "public", hostname = "not a name", certificate = "auto", email = "a@b.test", acceptTerms = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/setup/api/access", new { mode = "public", hostname = "license.zentriq.test", certificate = "auto", email = "a@b.test", acceptTerms = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/setup/api/access", new { mode = "public", hostname = "license.zentriq.test", certificate = "pfx", pfxBase64 = SelfSignedPfx("other.zentriq.test", "pfx pass"), pfxPassword = "pfx pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/setup/api/access", new { mode = "public", hostname = "license.zentriq.test", certificate = "pfx", pfxBase64 = SelfSignedPfx("license.zentriq.test", "pfx pass"), pfxPassword = "wrong" })).StatusCode);
        var pub = await Ok(await SendAsync(HttpMethod.Post, "/setup/api/access", new { mode = "public", hostname = "https://License.Zentriq.test/", certificate = "pfx", pfxBase64 = SelfSignedPfx("license.zentriq.test", "pfx pass"), pfxPassword = "pfx pass" }));
        Assert.True(pub.GetProperty("restart").GetBoolean());
        Assert.Equal("https://license.zentriq.test", pub.GetProperty("publicUrl").GetString());
        Assert.True(File.Exists(Path.Combine(_dataDir, "certs", "server.pfx")));
        await Ok(await SendAsync(HttpMethod.Post, "/setup/api/access", new { mode = "local" }));

        await Ok(await SendAsync(HttpMethod.Post, "/setup/api/finish", new { }));
        Assert.True((await State()).GetProperty("complete").GetBoolean());
        Assert.NotEqual(HttpStatusCode.Redirect, (await SendAsync(HttpMethod.Get, "/admin/")).StatusCode);   // no longer sent to the wizard

        // Next start (reboot): the key is unlocked by itself; the portal works without the wizard.
        await StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/setup/api/key/backup")).StatusCode);
        await Ok(await SendAsync(HttpMethod.Post, "/admin/api/login", new { email = "owner@zentriq.test", password = OwnerPass }));
        var after = await Ok(await SendAsync(HttpMethod.Get, "/admin/api/catalog"));
        Assert.Equal(fp, after.GetProperty("signing").GetProperty("fingerprint").GetString());
        Assert.True((await State()).GetProperty("key").GetProperty("loaded").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/setup/api/key/import", new { keyId = "zentriq-other", privateKeyPem = backup, passphrase = Phrase })).StatusCode);
    }

    [DbFact]
    public async Task An_existing_key_is_imported_with_its_passphrase()
    {
        var dir = Directory.CreateTempSubdirectory("zlskey").FullName;
        var c = KeyCeremony.Generate(dir, "zentriq-prod-2026-10", Phrase);
        var pem = await File.ReadAllTextAsync(c.PrivateFile);
        await Ok(await SendAsync(HttpMethod.Post, "/setup/api/owner", new { email = "owner@zentriq.test", password = OwnerPass }));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/setup/api/key/import", new { keyId = "zentriq-prod-2026-10", privateKeyPem = pem, passphrase = "the wrong passphrase" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/setup/api/key/import", new { keyId = "zentriq-prod-2026-10", privateKeyPem = Zentriq.Licensing.Signing.LicenseSigning.NewKeyPair().PrivatePem, passphrase = Phrase })).StatusCode);
        var k = await Ok(await SendAsync(HttpMethod.Post, "/setup/api/key/import", new { keyId = "zentriq-prod-2026-10", privateKeyPem = pem, passphrase = Phrase }));
        Assert.Equal(c.Fingerprint, k.GetProperty("fingerprint").GetString());
        var st = await State();
        Assert.True(st.GetProperty("key").GetProperty("backedUp").GetBoolean());
        await Ok(await SendAsync(HttpMethod.Post, "/setup/api/finish", new { }));
        Directory.Delete(dir, true);
    }
}
