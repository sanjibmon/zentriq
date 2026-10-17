using System.Net;
using System.Security.Cryptography.X509Certificates;
using Zentriq.Infrastructure.Agents;
using Zentriq.Infrastructure.Certificates;

namespace Zentriq.Tests;

public sealed class ServerCertificatesTests
{
    private static readonly string[] Names = ["zq-server.contoso.local", "zq-server", "localhost"];

    [Fact]
    public void Console_certificate_from_the_local_ca_covers_names_and_addresses_and_chains_to_the_ca()
    {
        using var ca = ServerCertificates.CreateCa("ZQ-SERVER");
        Assert.True(ServerCertificates.IsLocalCa(ca));
        IPAddress[] ips = [IPAddress.Loopback, IPAddress.Parse("192.168.1.8")];
        using var cert = ServerCertificates.Issue(ca, null, Names, ips);
        Assert.True(cert.HasPrivateKey);
        Assert.True(ServerCertificates.Covers(cert, ca, Names, ips));

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(cert), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
        Assert.Contains(cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public void A_new_address_needs_a_new_certificate_with_the_same_key_so_agent_pins_stay_valid()
    {
        using var ca = ServerCertificates.CreateCa("ZQ-SERVER");
        using var first = ServerCertificates.Issue(ca, null, Names, [IPAddress.Loopback, IPAddress.Parse("192.168.1.8")]);
        IPAddress[] moved = [IPAddress.Loopback, IPAddress.Parse("10.0.5.20")];
        Assert.False(ServerCertificates.Covers(first, ca, Names, moved));                      // the server got a new IP address
        Assert.False(ServerCertificates.Covers(first, ca, [.. Names, "zq-new"], [IPAddress.Loopback]));   // or a new name

        using var key = first.GetRSAPrivateKey();
        using var second = ServerCertificates.Issue(ca, key, Names, moved);
        Assert.True(ServerCertificates.Covers(second, ca, Names, moved));
        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
        Assert.Equal(AgentCertificateAuthority.SpkiSha256(first), AgentCertificateAuthority.SpkiSha256(second));

        using var otherCa = ServerCertificates.CreateCa("OTHER");
        Assert.False(ServerCertificates.Covers(second, otherCa, Names, moved));                // issued by another CA
    }

    [Fact]
    public void Pfx_round_trip_keeps_an_exportable_key()
    {
        using var ca = ServerCertificates.CreateCa("ZQ-SERVER");
        using var cert = ServerCertificates.Issue(ca, null, Names, [IPAddress.Loopback]);
        var dir = Directory.CreateTempSubdirectory("zq-cert").FullName;
        try
        {
            var pfx = Path.Combine(dir, "https.pfx");
            ServerCertificates.Save(cert, pfx, "pw-1");
            ServerCertificates.Save(cert, pfx, "pw-1"); // replace
            using var loaded = ServerCertificates.Load(pfx, "pw-1");
            Assert.Equal(cert.Thumbprint, loaded.Thumbprint);
            using var rsa = loaded.GetRSAPrivateKey()!;
            Assert.NotEmpty(rsa.ExportRSAPrivateKeyPem());
            Assert.True(Zentriq.Infrastructure.Remote.RemoteServerConfig.WriteWebCertificate(loaded, dir, out var why), why);
            Assert.Equal(cert.Thumbprint, File.ReadAllText(Path.Combine(dir, "zentriq-web-cert.txt")).Trim());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Names_exclude_addresses_and_addresses_include_loopback()
    {
        var names = ServerCertificates.Names("192.168.1.8");
        Assert.DoesNotContain("192.168.1.8", names);
        Assert.Contains("localhost", names);
        var ips = ServerCertificates.Addresses("192.168.1.8");
        Assert.Contains(IPAddress.Loopback, ips);
        Assert.Contains(IPAddress.Parse("192.168.1.8"), ips);
    }
}
