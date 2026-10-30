using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;
using Zentriq.LicenseServer.Services;

namespace Zentriq.LicenseServer.Tests;

public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG")))
            Skip = "ZENTRIQ_TEST_PG is not set (database integration test).";
    }
}

/// <summary>Unit tests of the license file the server issues (no database).</summary>
public sealed class IssuerTests
{
    private static readonly Customer Acme = new(Guid.NewGuid(), "Acme Ltd", "Ann", "ann@acme.test", null, null, "IN", null, null, DateTimeOffset.UtcNow);

    private static License Lic(string type = "subscription", int? nonit = 500) => new(
        "ZQ-2026-TEST01", Acme.Id, "ZQL-ABCDE…VWXYZ", "standard", type, type == "subscription" ? 3 : null,
        DateTimeOffset.UtcNow.AddDays(-10), type == "subscription" ? DateTimeOffset.UtcNow.AddYears(3) : LicenseFormats.PerpetualExpiry, DateTimeOffset.UtcNow.AddYears(1),
        250, nonit, 20, 2, [], 1, 30, "active", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static (LicenseIssuer Issuer, LicenseVerifier Verifier) Pair()
    {
        var (priv, pub) = LicenseSigning.NewKeyPair();
        var key = new SigningKey(LicenseSigning.LoadPrivateKey(priv), "test-key");
        var verifier = new LicenseVerifier(new InMemoryPublicKeyStore([new LicensePublicKey("test-key", pub, null, false)]));
        return (new LicenseIssuer(key, new LicenseServerOptions(), TimeProvider.System), verifier);
    }

    [Fact]
    public void Online_subscription_carries_metrics_lease_and_licensee_and_verifies()
    {
        var (issuer, verifier) = Pair();
        var install = Guid.NewGuid();
        var p = issuer.Payload(Lic(), Acme, install, null, LicenseFormats.Online, DateTimeOffset.UtcNow);
        Assert.Equal(250, p.Entitlements[Workloads.TotalDevices]);
        Assert.Equal(500, p.Entitlements[Workloads.NonItAssets]);
        Assert.Equal(20, p.Entitlements[Workloads.CloudInstances]);
        Assert.Equal(2, p.Entitlements[Workloads.Technicians]);
        Assert.NotNull(p.LeaseUntil);
        Assert.Equal("Acme Ltd", p.Licensee!.Company);

        var doc = LicenseSigning.Sign(p, LicenseSigning.LoadPrivateKey(LicenseSigning.NewKeyPair().PrivatePem), "other");
        Assert.False(verifier.Verify(doc, install, DateTimeOffset.UtcNow).IsValid);   // unknown key
    }

    [Fact]
    public void Unlimited_non_it_is_left_out_and_perpetual_never_expires()
    {
        var (issuer, _) = Pair();
        var p = issuer.Payload(Lic("perpetual", null), Acme, Guid.NewGuid(), null, LicenseFormats.Offline, DateTimeOffset.UtcNow);
        Assert.False(p.Entitlements.ContainsKey(Workloads.NonItAssets));
        Assert.Equal(LicenseFormats.PerpetualExpiry, p.ExpiresAt);
        Assert.Null(p.LeaseUntil);
        Assert.Null(p.TermYears);
        Assert.Equal(LicenseFormats.Perpetual, p.LicenseType);
    }

    [Fact]
    public void Lease_never_runs_past_the_subscription_grace()
    {
        var (issuer, _) = Pair();
        var l = Lic() with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(5), GraceDays = 0 };
        var p = issuer.Payload(l, Acme, Guid.NewGuid(), null, LicenseFormats.Online, DateTimeOffset.UtcNow);
        Assert.True(p.LeaseUntil <= l.ExpiresAt);
    }
}

/// <summary>The license server end to end against PostgreSQL: portal → license → online/offline activation → check-ins.</summary>
public sealed class LicenseServerFlowTests : IAsyncLifetime
{
    private readonly string _dbName = "zls_test_" + Guid.NewGuid().ToString("N")[..12];
    private string _keyDir = "";
    private string _publicPem = "";
    private WebApplication? _app;
    private HttpClient _client = null!;
    private const string Owner = "owner@zentriq.test";
    private const string OwnerPass = "correct horse battery staple";

    public async Task InitializeAsync()
    {
        var pg = Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return;
        await using (var c = new NpgsqlConnection(pg))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_dbName}", c);
            await cmd.ExecuteNonQueryAsync();
        }
        _keyDir = Directory.CreateTempSubdirectory("zls").FullName;
        var (priv, pub) = LicenseSigning.NewKeyPair();
        _publicPem = pub;
        await File.WriteAllTextAsync(Path.Combine(_keyDir, "k.pem"), priv);
        var cs = new NpgsqlConnectionStringBuilder(pg) { Database = _dbName }.ConnectionString;
        _app = Program.Build([
            $"--ConnectionStrings:LicenseServer={cs}",
            $"--LicenseServer:SigningKeyFile={Path.Combine(_keyDir, "k.pem")}",
            "--LicenseServer:SigningKeyId=test-2026",
            "--LicenseServer:RequestsPerMinute=10000",
        ], b => b.WebHost.UseTestServer());
        await MigrationRunner.MigrateAsync(_app.Services.GetRequiredService<NpgsqlDataSource>());
        await _app.Services.GetRequiredService<AdminStore>().CreateAsync(Owner, "Owner", OwnerPass, true, default);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG"));
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)", c);
        await cmd.ExecuteNonQueryAsync();
        try { Directory.Delete(_keyDir, true); } catch (IOException) { }
    }

    private LicenseVerifier Verifier() => new(new InMemoryPublicKeyStore([new LicensePublicKey("test-2026", _publicPem, null, false)]));

    private string? _cookie;
    private async Task<HttpResponseMessage> AdminAsync(HttpMethod m, string path, object? body = null, bool csrf = true)
    {
        var req = new HttpRequestMessage(m, "/admin/api" + path);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (csrf) req.Headers.Add("X-ZLS-Request", "1");
        if (_cookie is not null) req.Headers.Add("Cookie", _cookie);
        var resp = await _client.SendAsync(req);
        if (resp.Headers.TryGetValues("Set-Cookie", out var sc)) _cookie = sc.First().Split(';')[0];
        return resp;
    }

    private async Task<JsonElement> OkJson(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task LoginAsync() => await OkJson(await AdminAsync(HttpMethod.Post, "/login", new { email = Owner, password = OwnerPass }));

    private async Task<(string Id, string Key)> NewLicenseAsync(string type = "subscription", int max = 1)
    {
        var cust = await OkJson(await AdminAsync(HttpMethod.Post, "/customers", new { company = "Acme " + Guid.NewGuid().ToString("N")[..6], contactEmail = "it@acme.test", country = "IN" }));
        var lic = await OkJson(await AdminAsync(HttpMethod.Post, "/licenses", new
        {
            customerId = cust.GetProperty("id").GetGuid(), licenseType = type, termYears = type == "subscription" ? 1 : (int?)null, supportYears = type == "perpetual" ? 1 : (int?)null,
            edition = "advanced", devices = 300, nonItPack = 1000, cloudInstances = 25, technicians = 3, maxActivations = max,
        }));
        return (lic.GetProperty("id").GetString()!, lic.GetProperty("licenseKey").GetString()!);
    }

    private static ActivationRequest Request(Guid install) => new(LicenseFormats.ActivationRequest, LicenseFormats.CurrentSchemaVersion, Guid.NewGuid(), install,
        "1.1.1", "ZQ-SRV01", "Acme", DateTimeOffset.UtcNow, new Dictionary<string, int> { ["devices"] = 120, ["nonit_assets"] = 40 });

    private static HttpRequestMessage Bearer(string path, object body, string token)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, "/" + path) { Content = JsonContent.Create(body) };
        m.Headers.Authorization = new("Bearer", token);
        return m;
    }

    [DbFact]
    public async Task Portal_requires_sign_in_and_the_request_header()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await AdminAsync(HttpMethod.Get, "/licenses")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AdminAsync(HttpMethod.Post, "/login", new { email = Owner, password = "wrong passphrase!!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AdminAsync(HttpMethod.Post, "/login", new { email = Owner, password = OwnerPass }, csrf: false)).StatusCode);
        await LoginAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await AdminAsync(HttpMethod.Post, "/customers", new { company = "X" }, csrf: false)).StatusCode);
        await OkJson(await AdminAsync(HttpMethod.Get, "/catalog"));
    }

    [DbFact]
    public async Task Online_activation_checkin_suspend_resume_and_portal_deactivation()
    {
        await LoginAsync();
        var (id, key) = await NewLicenseAsync();
        var install = Guid.NewGuid();

        var act = await OkJson(await _client.PostAsJsonAsync("/" + LicenseServerProtocol.ActivatePath, new OnlineActivateRequest(key.ToLowerInvariant(), Request(install))));
        var token = act.GetProperty("activationToken").GetString()!;
        var v = Verifier().Verify(act.GetProperty("license").GetString()!, install, DateTimeOffset.UtcNow);
        Assert.True(v.IsValid, v.Message);
        Assert.Equal(id, v.Payload!.LicenseId);
        Assert.Equal(LicenseFormats.Online, v.Payload.Activation);
        Assert.Equal("advanced", v.Payload.Edition);
        Assert.Equal(1000, v.Payload.Entitlements[Workloads.NonItAssets]);
        Assert.NotNull(v.Payload.LeaseUntil);

        // The key is bound: a second server is refused while the first holds the only activation.
        var other = await _client.PostAsJsonAsync("/" + LicenseServerProtocol.ActivatePath, new OnlineActivateRequest(key, Request(Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);

        var checkin = new OnlineCheckinRequest(install, id, "1.1.1", DateTimeOffset.UtcNow, new Dictionary<string, int> { ["devices"] = 130 });
        var c1 = await OkJson(await _client.SendAsync(Bearer(LicenseServerProtocol.CheckinPath, checkin, token)));
        Assert.Equal("active", c1.GetProperty("status").GetString());
        Assert.True(Verifier().Verify(c1.GetProperty("license").GetString()!, install, DateTimeOffset.UtcNow).IsValid);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(Bearer(LicenseServerProtocol.CheckinPath, checkin, "not-the-token"))).StatusCode);

        await OkJson(await AdminAsync(HttpMethod.Post, $"/licenses/{id}/status", new { status = "suspended", reason = "unpaid" }));
        var c2 = await OkJson(await _client.SendAsync(Bearer(LicenseServerProtocol.CheckinPath, checkin, token)));
        Assert.Equal("suspended", c2.GetProperty("status").GetString());
        await OkJson(await AdminAsync(HttpMethod.Post, $"/licenses/{id}/status", new { status = "active" }));
        Assert.Equal("active", (await OkJson(await _client.SendAsync(Bearer(LicenseServerProtocol.CheckinPath, checkin, token)))).GetProperty("status").GetString());

        var detail = await OkJson(await AdminAsync(HttpMethod.Get, "/licenses/" + id));
        var activation = detail.GetProperty("activations")[0];
        Assert.Equal(130, activation.GetProperty("usage").GetProperty("devices").GetInt32());
        await OkJson(await AdminAsync(HttpMethod.Post, $"/activations/{activation.GetProperty("id").GetGuid()}/deactivate"));
        Assert.Equal("deactivated", (await OkJson(await _client.SendAsync(Bearer(LicenseServerProtocol.CheckinPath, checkin, token)))).GetProperty("status").GetString());

        // The freed activation can be used by another server.
        await OkJson(await _client.PostAsJsonAsync("/" + LicenseServerProtocol.ActivatePath, new OnlineActivateRequest(key, Request(Guid.NewGuid()))));
    }

    [DbFact]
    public async Task Installation_deactivation_releases_the_key_and_revocation_is_final()
    {
        await LoginAsync();
        var (id, key) = await NewLicenseAsync();
        var install = Guid.NewGuid();
        var act = await OkJson(await _client.PostAsJsonAsync("/" + LicenseServerProtocol.ActivatePath, new OnlineActivateRequest(key, Request(install))));
        var token = act.GetProperty("activationToken").GetString()!;
        await OkJson(await _client.SendAsync(Bearer(LicenseServerProtocol.DeactivatePath, new OnlineDeactivateRequest(install, id), token)));
        var install2 = Guid.NewGuid();
        await OkJson(await _client.PostAsJsonAsync("/" + LicenseServerProtocol.ActivatePath, new OnlineActivateRequest(key, Request(install2))));

        await OkJson(await AdminAsync(HttpMethod.Post, $"/licenses/{id}/status", new { status = "revoked" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Post, $"/licenses/{id}/status", new { status = "active" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/" + LicenseServerProtocol.ActivatePath, new OnlineActivateRequest(key, Request(install2)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/" + LicenseServerProtocol.ActivatePath, new OnlineActivateRequest(LicenseKeys.New(), Request(install2)))).StatusCode);
    }

    [DbFact]
    public async Task Offline_activation_with_key_and_from_the_portal_for_perpetual_licenses()
    {
        await LoginAsync();
        var (id, key) = await NewLicenseAsync("perpetual", max: 2);
        var install = Guid.NewGuid();
        var r = await OkJson(await _client.PostAsJsonAsync("/" + LicenseServerProtocol.OfflineActivatePath, new { licenseKey = key, request = Request(install) }));
        var v = Verifier().Verify(r.GetProperty("license").GetString()!, install, DateTimeOffset.UtcNow);
        Assert.True(v.IsValid, v.Message);
        Assert.Equal(LicenseFormats.Offline, v.Payload!.Activation);
        Assert.Equal(LicenseFormats.Perpetual, v.Payload.LicenseType);
        Assert.Null(v.Payload.LeaseUntil);
        Assert.Equal(LicenseFormats.PerpetualExpiry, v.Payload.ExpiresAt);

        var before = v.Payload.SupportUntil!.Value;
        var s = await OkJson(await AdminAsync(HttpMethod.Post, $"/licenses/{id}/support", new { years = 2 }));
        Assert.Equal(before.AddYears(2).Date, s.GetProperty("supportUntil").GetDateTimeOffset().Date);
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Post, $"/licenses/{id}/renew", new { years = 1 })).StatusCode);

        var install2 = Guid.NewGuid();
        var p = await OkJson(await AdminAsync(HttpMethod.Post, $"/licenses/{id}/offline", new { request = Request(install2) }));
        var v2 = Verifier().Verify(p.GetProperty("license").GetString()!, install2, DateTimeOffset.UtcNow);
        Assert.True(v2.IsValid, v2.Message);
        Assert.Equal(before.AddYears(2).Date, v2.Payload!.SupportUntil!.Value.Date);
        // Both activations are used.
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Post, $"/licenses/{id}/offline", new { request = Request(Guid.NewGuid()) })).StatusCode);
    }

    [DbFact]
    public async Task Renewal_and_new_key()
    {
        await LoginAsync();
        var (id, key) = await NewLicenseAsync();
        var detail = await OkJson(await AdminAsync(HttpMethod.Get, "/licenses/" + id));
        var ends = detail.GetProperty("license").GetProperty("expiresAt").GetDateTimeOffset();
        var r = await OkJson(await AdminAsync(HttpMethod.Post, $"/licenses/{id}/renew", new { years = 3 }));
        Assert.Equal(ends.AddYears(3), r.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Post, $"/licenses/{id}/renew", new { years = 2 })).StatusCode);

        var nk = await OkJson(await AdminAsync(HttpMethod.Post, $"/licenses/{id}/rekey"));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/" + LicenseServerProtocol.ActivatePath, new OnlineActivateRequest(key, Request(Guid.NewGuid())))).StatusCode);
        await OkJson(await _client.PostAsJsonAsync("/" + LicenseServerProtocol.ActivatePath, new OnlineActivateRequest(nk.GetProperty("licenseKey").GetString()!, Request(Guid.NewGuid()))));
    }
}
