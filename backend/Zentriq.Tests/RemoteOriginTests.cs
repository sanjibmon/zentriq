using System.Text.Json.Nodes;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Remote;
using Zentriq.Worker.Remote;

namespace Zentriq.Tests;

/// <summary>Browser origins the remote support engine accepts ("Invalid origin in HTTP request" otherwise).</summary>
public sealed class RemoteOriginTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("zq-origin").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void Origins_cover_server_names_addresses_and_aliases()
    {
        var o = RemoteServerConfig.Origins("ZQ-Server.contoso.local", ["Zentriq.Contoso.Local.", "bad host/x", "10.0.5.20"]);
        Assert.Contains("zq-server.contoso.local", o);
        Assert.Contains("localhost", o);
        Assert.Contains("127.0.0.1", o);
        Assert.Contains(Environment.MachineName.ToLowerInvariant(), o);
        Assert.Contains("zentriq.contoso.local", o);    // alias, normalized
        Assert.Contains("10.0.5.20", o);
        Assert.DoesNotContain("bad host/x", o);
        Assert.Equal(o.Order(StringComparer.Ordinal), o);  // stable order: no restart loop
        Assert.Null(RemoteServerConfig.NormalizeHost("evil.example/path"));
        Assert.Equal("192.168.1.8", RemoteServerConfig.NormalizeHost(" 192.168.1.8 "));
    }

    [Fact]
    public void Remembered_aliases_are_stored_once()
    {
        Assert.True(RemoteServerConfig.RememberOrigin(_root, "Zentriq.Contoso.Local"));
        Assert.False(RemoteServerConfig.RememberOrigin(_root, "zentriq.contoso.local"));
        Assert.False(RemoteServerConfig.RememberOrigin(_root, "not a host!"));
        Assert.Equal(["zentriq.contoso.local"], RemoteServerConfig.ReadLines(Path.Combine(_root, RemoteServerConfig.OriginsFile)));
    }

    [Fact]
    public void Remote_service_adds_new_origins_and_then_stays_quiet()
    {
        var paths = new ZentriqPaths(_root);
        var data = Path.Combine(_root, "Remote", "data");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(paths.RemoteCertificateDir);
        // A configuration as builds 51-53 wrote it: no allowed origins, so the engine accepts only its certificate name.
        var cfg = RemoteServerConfig.Build(new("zq-server", 8446, new string('a', 160), "s", data, Path.Combine(_root, "f"), Path.Combine(_root, "b")));
        File.WriteAllText(Path.Combine(data, "config.json"), cfg.ToJsonString());

        Assert.True(RemoteHost.Refresh(paths, data, out var what));
        Assert.Contains("accepted host names", what);
        var origins = Allowed(data);
        Assert.Contains("zq-server", origins);
        Assert.Contains("127.0.0.1", origins);
        Assert.False(RemoteHost.Refresh(paths, data, out _));           // nothing new: no restart

        RemoteServerConfig.RememberOrigin(paths.RemoteCertificateDir, "zentriq.contoso.local");
        Assert.True(RemoteHost.Refresh(paths, data, out _));            // a technician used an alias
        Assert.Contains("zentriq.contoso.local", Allowed(data));
    }

    private static string[] Allowed(string data) =>
        (JsonNode.Parse(File.ReadAllText(Path.Combine(data, "config.json")))!["domains"]![""]!["allowedOrigin"] as JsonArray)!.Select(n => n!.GetValue<string>()).ToArray();
}
