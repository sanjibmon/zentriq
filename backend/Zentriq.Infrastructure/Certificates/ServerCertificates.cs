using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Zentriq.Infrastructure.Certificates;

/// <summary>
/// The server's own HTTPS identity when no customer certificate is configured: a local CA ("Zentriq Local CA", created
/// once, trusted once on technician PCs) issues the console certificate for the server's current names and IP
/// addresses. When the server gets a new name or address the certificate is re-issued from the same CA with the same
/// key, so browsers that trust the CA keep trusting the console, and agents (which pin the key) keep connecting.
/// </summary>
public static class ServerCertificates
{
    public const string CaSubjectPrefix = "CN=Zentriq Local CA";
    public static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(825);
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(60);

    /// <summary>Names the console certificate must cover: the configured host name, the machine's FQDN and name, localhost.</summary>
    public static string[] Names(string? configured)
    {
        var list = new List<string>();
        void Add(string? n)
        {
            if (string.IsNullOrWhiteSpace(n)) return;
            n = n.Trim().ToLowerInvariant();
            if (IPAddress.TryParse(n, out _)) return; // addresses go in Addresses()
            if (!list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n);
        }
        Add(configured);
        try { Add(Dns.GetHostEntry(Environment.MachineName).HostName); } catch (SocketException) { }
        Add(Environment.MachineName);
        Add("localhost");
        return list.ToArray();
    }

    /// <summary>Addresses the certificate must cover: loopback, the configured host when it is an address, and the IPv4
    /// addresses of the server's active network adapters (no link-local).</summary>
    public static IPAddress[] Addresses(string? configured = null)
    {
        var list = new List<IPAddress> { IPAddress.Loopback };
        if (configured is not null && IPAddress.TryParse(configured.Trim(), out var c)) list.Add(c);
        try
        {
            list.AddRange(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal)));
        }
        catch (NetworkInformationException) { }
        return list.Distinct().OrderBy(a => a.ToString(), StringComparer.Ordinal).ToArray();
    }

    public static X509Certificate2 CreateCa(string serverName)
    {
        using var key = RSA.Create(3072);
        var req = new CertificateRequest($"{CaSubjectPrefix} ({Sanitize(serverName)}), O=Zentriq", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(20));
    }

    public static bool IsLocalCa(X509Certificate2 cert) =>
        cert.Subject.StartsWith(CaSubjectPrefix, StringComparison.Ordinal) && cert.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority);

    /// <summary>Issues the console certificate from the local CA. Pass the current certificate's key to keep it.</summary>
    public static X509Certificate2 Issue(X509Certificate2 ca, RSA? key, IReadOnlyList<string> names, IReadOnlyList<IPAddress> addresses)
    {
        using var newKey = key is null ? RSA.Create(3072) : null;
        var k = key ?? newKey!;
        var req = new CertificateRequest($"CN={names.FirstOrDefault() ?? "localhost"}, O=Zentriq", k, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in names) san.AddDnsName(n);
        foreach (var a in addresses) san.AddIpAddress(a);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-10);
        var notAfter = notBefore + LeafLifetime;
        if (notAfter > ca.NotAfter) notAfter = ca.NotAfter.AddMinutes(-1);
        using var issued = req.Create(ca, notBefore, notAfter, serial);
        return issued.CopyWithPrivateKey(k);
    }

    /// <summary>True when the certificate was issued by this CA, covers all names and addresses and is not due for renewal.</summary>
    public static bool Covers(X509Certificate2 cert, X509Certificate2 ca, IEnumerable<string> names, IEnumerable<IPAddress> addresses)
    {
        if (cert.NotAfter.ToUniversalTime() < DateTime.UtcNow + RenewBefore) return false;
        if (!cert.IssuerName.RawData.AsSpan().SequenceEqual(ca.SubjectName.RawData)) return false;
        var ext = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (ext is null) return false;
        var dns = ext.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ips = ext.EnumerateIPAddresses().ToHashSet();
        return names.All(dns.Contains) && addresses.All(ips.Contains);
    }

    public static X509Certificate2 Load(string pfxPath, string? password) =>
        X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

    /// <summary>
    /// Writes a PFX. Replaces it atomically when the folder allows it (Setup); the API service may only write the file
    /// itself, so it is then rewritten in place.
    /// </summary>
    public static void Save(X509Certificate2 cert, string pfxPath, string? password)
    {
        var bytes = cert.Export(X509ContentType.Pfx, password);
        var tmp = pfxPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, pfxPath, overwrite: true);
        }
        catch (UnauthorizedAccessException)
        {
            File.WriteAllBytes(pfxPath, bytes);
        }
    }

    private static string Sanitize(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.' or ' ').Take(60).ToArray());
}
