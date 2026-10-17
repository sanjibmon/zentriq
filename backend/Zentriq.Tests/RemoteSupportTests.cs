using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Remote;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Tests;

public sealed class RemoteSupportTests
{
    [Fact]
    public void Cookies_round_trip_and_carry_time()
    {
        var key = MeshCookie.NewKeyHex();
        Assert.Equal(160, key.Length);
        var c = MeshCookie.Login("user//zq-abc", key, 2);
        Assert.DoesNotContain('+', c);
        Assert.DoesNotContain('/', c);
        var o = MeshCookie.Decode(c, key)!;
        Assert.Equal("user//zq-abc", o["u"]!.GetValue<string>());
        Assert.Equal(3, o["a"]!.GetValue<int>());
        Assert.Equal(2, o["expire"]!.GetValue<int>());
        Assert.InRange(o["time"]!.GetValue<long>(), DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5);
        Assert.Null(MeshCookie.Decode(c, MeshCookie.NewKeyHex()));   // another installation's key cannot read it
        var auth = MeshCookie.Decode(MeshCookie.ControlAuth("zentriq", key), key)!;
        Assert.Equal("user//zentriq", auth["userid"]!.GetValue<string>());
        Assert.Equal("", auth["domainid"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => MeshCookie.Login("u", "00"));
    }

    [Fact]
    public void Server_configuration_is_branded_and_locked_down()
    {
        var cfg = RemoteServerConfig.Build(new("zentriq.contoso.local", 8446, new string('a', 160), "s", @"C:\d", @"C:\f", @"C:\b"));
        var s = cfg["settings"]!.AsObject();
        Assert.Equal(8446, s["port"]!.GetValue<int>());
        Assert.Equal(0, s["redirPort"]!.GetValue<int>());
        Assert.Equal(0, s["mpsPort"]!.GetValue<int>());
        Assert.True(s["allowLoginToken"]!.GetValue<bool>());
        Assert.False(s["selfUpdate"]!.GetValue<bool>());
        Assert.Equal("zentriq.contoso.local", s["cert"]!.GetValue<string>());
        Assert.Equal("localhost.", RemoteServerConfig.CertificateName("localhost"));
        Assert.Equal("ZQ-SERVER.", RemoteServerConfig.CertificateName("ZQ-SERVER"));
        Assert.Equal("192.168.1.8", RemoteServerConfig.CertificateName("192.168.1.8"));
        var d = cfg["domains"]![""]!.AsObject();
        Assert.Equal("Zentriq", d["title"]!.GetValue<string>());
        Assert.False(d["newAccounts"]!.GetValue<bool>());
        Assert.False(d["showPasswordLogin"]!.GetValue<bool>());
        Assert.Equal("Zentriq Remote Support", d["agentCustomization"]!["displayName"]!.GetValue<string>());
        Assert.Equal("ZentriqRemoteSupport", d["agentCustomization"]!["serviceName"]!.GetValue<string>());
        Assert.Contains("{0}", d["consentMessages"]!["Desktop"]!.GetValue<string>());
        Assert.DoesNotContain("MeshCentral", d["title"]!.GetValue<string>() + d["title2"] + d["footer"]);
    }

    [Fact]
    public void Rights_consent_and_names()
    {
        Assert.Equal(8 | 16 | 32 | 1 | 2 | 4 | 64, MeshRights.Consent(true, true, true));
        Assert.Equal(0, MeshRights.Consent(false, false, false));
        Assert.Equal(8 | 16 | 32, MeshRights.Consent(true, false, false));
        Assert.Equal(11, RemoteSupportService.ViewMode("desktop"));
        Assert.Equal(12, RemoteSupportService.ViewMode("terminal"));
        Assert.Equal(13, RemoteSupportService.ViewMode("files"));
        Assert.Equal(10, RemoteSupportService.ViewMode("device"));
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal("zq-11111111222233334444555555555555", RemoteSupportService.MeshUserName(id));
        Assert.Equal("abc$def", new MeshNode("node//abc$def", "PC", null, null, true, null, 4, null).UrlId);
    }
}

public sealed class MeshFactAttribute : FactAttribute
{
    public MeshFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZENTRIQ_TEST_MESHCENTRAL")) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZENTRIQ_TEST_PG")))
            Skip = "ZENTRIQ_TEST_MESHCENTRAL / ZENTRIQ_TEST_PG not set (live remote support test).";
    }
}

/// <summary>Against a real MeshCentral server (the version Zentriq bundles): sign-in by key, groups, users, links, agent installer.</summary>
public sealed class RemoteSupportLiveTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private Process? _server;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zq-mesh-" + Guid.NewGuid().ToString("N")[..8]);
    private const int Port = 18446;

    public Task InitializeAsync() => _db.InitializeAsync();

    public async Task DisposeAsync()
    {
        try { if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _server?.Dispose();
        await _db.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static async Task<string> Run(string script, params string[] args)
    {
        var psi = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(script)! };
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        return output;
    }

    [MeshFact]
    public async Task Zentriq_manages_and_signs_in_to_the_remote_support_server()
    {
        var script = Path.Combine(Environment.GetEnvironmentVariable("ZENTRIQ_TEST_MESHCENTRAL")!, "node_modules", "meshcentral", "meshcentral.js");
        Assert.True(File.Exists(script), script);
        var data = Path.Combine(_dir, "data");
        Directory.CreateDirectory(data);
        var key = MeshCookie.NewKeyHex();
        var cfg = RemoteServerConfig.Build(new("localhost", Port, key, Convert.ToHexString(Guid.NewGuid().ToByteArray()), data, Path.Combine(_dir, "files"), Path.Combine(_dir, "backups")));
        File.WriteAllText(Path.Combine(data, "config.json"), cfg.ToJsonString());

        var created = await Run(script, "--datapath", data, "--createaccount", "zentriq", "--" + "pass", "Zq-" + Guid.NewGuid().ToString("N"), "--name", "Zentriq");
        Assert.Contains("Done", created);
        var admin = await Run(script, "--datapath", data, "--adminaccount", "zentriq");
        Assert.Contains("Done", admin);

        var psi = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(script)! };
        psi.ArgumentList.Add(script); psi.ArgumentList.Add("--datapath"); psi.ArgumentList.Add(data);
        _server = Process.Start(psi)!;
        var log = new System.Text.StringBuilder();
        _server.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        _server.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        _server.BeginOutputReadLine(); _server.BeginErrorReadLine();

        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = true, CookieContainer = new CookieContainer() });
        var deadline = DateTime.UtcNow.AddMinutes(6);
        var up = false;
        while (DateTime.UtcNow < deadline && !up)
        {
            try { up = (await http.GetAsync($"https://127.0.0.1:{Port}/health.ashx")).IsSuccessStatusCode; } catch (HttpRequestException) { }
            if (!up) { Assert.False(_server.HasExited, "MeshCentral exited: " + log); await Task.Delay(2000); }
        }
        Assert.True(up, "MeshCentral did not start: " + log);

        // Management connection authenticated with the login key only.
        await using (var c = await MeshControlClient.ConnectAsync(new Uri($"https://127.0.0.1:{Port}/"), "zentriq", key, default))
        {
            var mesh = await c.CreateGroupAsync("CI group", "test", MeshRights.Consent(true, true, true), default);
            Assert.StartsWith("mesh//", mesh);
            Assert.Contains(mesh, (await c.GroupsAsync(default)).Keys);
            await c.EnsureUserAsync("zq-ci", "CI Technician", null, default);
            await c.EnsureUserAsync("zq-ci", "CI Technician", null, default);   // existing account is fine
            await c.GrantGroupAsync(mesh, "user//zq-ci", MeshRights.RemoteControl, default);
            await c.SetGroupConsentAsync(mesh, MeshRights.Consent(false, true, false), default);
            Assert.Empty(await c.NodesAsync(mesh, default));
        }
        // A wrong key is refused.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var bad = await MeshControlClient.ConnectAsync(new Uri($"https://127.0.0.1:{Port}/"), "zentriq", MeshCookie.NewKeyHex(), default);
            await bad.GroupsAsync(default);
        });

        // Zentriq service: group, technician account, sign-in link, agent installer, sync.
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.0.0-test");
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var options = new ZentriqOptions { Https = new HttpsOptions { HostName = "localhost" }, Remote = new RemoteOptions { Enabled = true, Port = Port } };
        var remote = new RemoteSupportService(ds, options, () => new AppSecrets { RemoteLoginKey = key }, NullLogger<RemoteSupportService>.Instance);
        Assert.True((await remote.StatusAsync(default)).Running);
        var uid = Guid.NewGuid();
        var url = await remote.SessionUrlAsync(uid, "Priya Rao", fullRights: false, viewOnly: false, nodeId: null, view: "console", default);
        Assert.StartsWith($"https://localhost:{Port}/?login=", url);
        var page = await (await http.GetAsync(url.Replace("https://localhost:", "https://127.0.0.1:"))).Content.ReadAsStringAsync();
        Assert.Contains("id=MainDevDesktop", page);          // signed in: the main console, not the sign-in page
        Assert.Contains("Zentriq", page);
        using var fresh = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var anon = await (await fresh.GetAsync($"https://127.0.0.1:{Port}/?login=" + Uri.EscapeDataString(MeshCookie.Login("user//zq-" + uid.ToString("N"), MeshCookie.NewKeyHex())))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=MainDevDesktop", anon);    // a forged link does not sign in

        // The server signs its Windows agents in the background after start; wait until the installer is served.
        (byte[] bytes, string sha) installer = default;
        for (var i = 0; ; i++)
        {
            try { installer = await remote.AgentInstallerAsync(default); break; }
            catch (MeshException) when (i < 60) { await Task.Delay(2000); }
        }
        var (bytes, sha) = installer;
        Assert.Equal((byte)'M', bytes[0]);
        Assert.Equal(64, sha.Length);
        // Devices must be told where the server is (WAN mode), not to look for it by multicast (LAN mode), even for a
        // host name without a dot such as "localhost".
        string meshId;
        await using (var mc = await MeshControlClient.ConnectAsync(new Uri($"https://127.0.0.1:{Port}/"), options.Remote.ServiceUser, key, default))
            meshId = await remote.EnsureGroupAsync(mc, null, default);
        var msh = await (await http.GetAsync($"https://127.0.0.1:{Port}/meshsettings?id={Uri.EscapeDataString(meshId[(meshId.LastIndexOf('/') + 1)..])}")).Content.ReadAsStringAsync();
        Assert.Contains($"MeshServer=wss://localhost:{Port}/agent.ashx", msh);
        Assert.Equal($"https://192.168.1.8:{Port}/", remote.UrlFor("192.168.1.8"));
        Assert.Equal($"https://localhost:{Port}/", remote.UrlFor("bad host/x"));
        var sync = await remote.SyncAsync(default);
        Assert.Equal(0, sync.Nodes);
        await remote.SaveSettingsAsync(new RemoteSettings { Prompt = false, Notify = true, PrivacyBar = true }, default);
        Assert.False((await remote.SettingsAsync(default)).Prompt);

        // Second start, as Setup does after the first one: the engine now presents the console's certificate and stays in
        // WAN mode (devices are told the server address).
        _server.Kill(entireProcessTree: true);
        await _server.WaitForExitAsync();
        using var rsaKey = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost, O=Zentriq (self-signed)", rsaKey,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var sanB = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        sanB.AddDnsName("localhost"); sanB.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(sanB.Build());
        using var consoleCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        Assert.True(RemoteServerConfig.WriteWebCertificate(consoleCert, data, out var why), why);
        File.WriteAllText(Path.Combine(data, "config.json"), RemoteServerConfig.Build(new("localhost", Port, key, Convert.ToHexString(Guid.NewGuid().ToByteArray()),
            data, Path.Combine(_dir, "files"), Path.Combine(_dir, "backups"), KeepCertificate: true)).ToJsonString());
        var psi2 = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(script)! };
        psi2.ArgumentList.Add(script); psi2.ArgumentList.Add("--datapath"); psi2.ArgumentList.Add(data);
        _server = Process.Start(psi2)!;
        var log2 = new System.Text.StringBuilder();
        _server.OutputDataReceived += (_, e) => { lock (log2) log2.AppendLine(e.Data); };
        _server.ErrorDataReceived += (_, e) => { lock (log2) log2.AppendLine(e.Data); };
        _server.BeginOutputReadLine(); _server.BeginErrorReadLine();
        string? served = null;
        using var http2 = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => { served = c?.Thumbprint; return true; } });
        up = false; deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline && !up)
        {
            try { up = (await http2.GetAsync($"https://127.0.0.1:{Port}/health.ashx")).IsSuccessStatusCode; } catch (HttpRequestException) { }
            if (!up) { Assert.False(_server.HasExited, "MeshCentral exited: " + log2); await Task.Delay(1000); }
        }
        Assert.True(up, "MeshCentral did not restart: " + log2);
        Assert.Equal(consoleCert.Thumbprint, served);       // the console's certificate, not one the engine issued
        Assert.Contains("WAN mode", log2.ToString());
        var msh2 = await (await http2.GetAsync($"https://127.0.0.1:{Port}/meshsettings?id={Uri.EscapeDataString(meshId[(meshId.LastIndexOf('/') + 1)..])}")).Content.ReadAsStringAsync();
        Assert.Contains($"MeshServer=wss://localhost:{Port}/agent.ashx", msh2);
        Assert.Contains("id=MainDevDesktop", await (await http2.GetAsync((await remote.SessionUrlAsync(uid, "Priya Rao", false, false, null, "console", default)).Replace("https://localhost:", "https://127.0.0.1:"))).Content.ReadAsStringAsync());
    }
}
