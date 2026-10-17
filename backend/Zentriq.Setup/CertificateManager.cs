using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Zentriq.Infrastructure.Configuration;

namespace Zentriq.Setup;

/// <summary>
/// HTTPS certificate handling. Default: a self-signed RSA-3072 server certificate (2 years) with
/// SANs for the configured host name, the machine's FQDN/NetBIOS name and localhost, written as a
/// password-protected PFX in the Secrets folder and trusted in LocalMachine\Root on this server.
/// Alternatively an existing certificate from LocalMachine\My (by thumbprint).
/// </summary>
public sealed class CertificateManager(Reporter log)
{
    public sealed record CertificateInfo(string Thumbprint, DateTime NotAfter, bool SelfSigned, string Subject);

    public CertificateInfo EnsureSelfSigned(ZentriqPaths paths, string hostName, string pfxPassword, bool forceNew)
    {
        var names = SubjectNames(hostName);
        // Technicians often open the console by IP address (https://192.168.1.8); include this server's addresses too.
        var ips = ServerAddresses();
        if (!forceNew && File.Exists(paths.HttpsCertificateFile))
        {
            try
            {
                using var existing = X509CertificateLoader.LoadPkcs12FromFile(paths.HttpsCertificateFile, pfxPassword, X509KeyStorageFlags.EphemeralKeySet);
                var sanExt = existing.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                var san = sanExt?.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                var sanIps = sanExt?.EnumerateIPAddresses().ToHashSet() ?? [];
                if (existing.NotAfter > DateTime.Now.AddDays(30) && names.All(san.Contains) && ips.All(sanIps.Contains))
                {
                    log.Info($"Keeping existing HTTPS certificate {existing.Thumbprint} (expires {existing.NotAfter:yyyy-MM-dd}).");
                    Trust(existing);
                    return new(existing.Thumbprint, existing.NotAfter, true, existing.Subject);
                }
                log.Info("Existing HTTPS certificate is expiring or does not cover the host name; issuing a new one.");
            }
            catch (CryptographicException) { log.Warn("Existing HTTPS certificate could not be opened; issuing a new one."); }
        }

        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest($"CN={names[0]}, O=Zentriq (self-signed)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var n in names) sanBuilder.AddDnsName(n);
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in ips) sanBuilder.AddIpAddress(ip);
        req.CertificateExtensions.Add(sanBuilder.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        File.WriteAllBytes(paths.HttpsCertificateFile, cert.Export(X509ContentType.Pfx, pfxPassword));
        Trust(cert);
        log.Info($"Issued self-signed HTTPS certificate {cert.Thumbprint} for {string.Join(", ", names.Concat(ips.Select(i => i.ToString())))}.");
        return new(cert.Thumbprint, cert.NotAfter, true, cert.Subject);
    }

    /// <summary>Validates a customer certificate in LocalMachine\My and grants the API service read access to its key.</summary>
    public CertificateInfo UseExisting(string thumbprint, string apiService)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Replace(" ", ""), false);
        if (found.Count == 0) throw new SetupException($"Certificate {thumbprint} was not found in Local Computer > Personal.", "Import the certificate with its private key first.");
        var cert = found[0];
        if (!cert.HasPrivateKey) throw new SetupException("The selected certificate has no private key.");
        if (cert.NotAfter < DateTime.Now) throw new SetupException("The selected certificate has expired.");
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is not null && !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1"))
            throw new SetupException("The selected certificate is not valid for server authentication.");
        GrantKeyAccess(cert, apiService);
        log.Info($"Using certificate {cert.Thumbprint} ({cert.Subject}), expires {cert.NotAfter:yyyy-MM-dd}.");
        return new(cert.Thumbprint, cert.NotAfter, false, cert.Subject);
    }

    private void GrantKeyAccess(X509Certificate2 cert, string apiService)
    {
        try
        {
            using var rsa = cert.GetRSAPrivateKey();
            string? keyFile = rsa switch
            {
                RSACng cng => FindKeyFile(cng.Key.UniqueName),
                _ => null,
            };
            if (keyFile is null) { log.Warn("Could not locate the certificate's private key file; grant 'NT SERVICE\\" + apiService + "' read access manually (certlm.msc > Manage Private Keys)."); return; }
            var sec = new FileInfo(keyFile).GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(WindowsHost.ServiceSid(apiService), FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(keyFile).SetAccessControl(sec);
            log.Info("Granted the API service read access to the certificate private key.");
        }
        catch (Exception ex) { log.Warn($"Could not grant private-key access automatically: {ex.Message}"); }
    }

    private static string? FindKeyFile(string? uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName)) return null;
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var dir in new[] { @"Microsoft\Crypto\Keys", @"Microsoft\Crypto\RSA\MachineKeys" })
        {
            var p = Path.Combine(programData, dir, uniqueName);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private void Trust(X509Certificate2 cert)
    {
        try
        {
            using var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            root.Open(OpenFlags.ReadWrite);
            if (root.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count == 0)
            {
                using var pub = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));
                root.Add(pub);
                log.Info("Trusted the self-signed certificate on this server (LocalMachine\\Root). Distribute it to client machines or replace it with a CA-issued certificate.");
            }
        }
        catch (Exception ex) { log.Warn($"Could not add the certificate to the trusted root store: {ex.Message}"); }
    }

    /// <summary>The IPv4 addresses of this server's active network adapters (no loopback or link-local).</summary>
    public static IPAddress[] ServerAddresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)
                            && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Distinct().OrderBy(a => a.ToString(), StringComparer.Ordinal).ToArray();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return []; }
    }

    public static string[] SubjectNames(string? configured)
    {
        var list = new List<string>();
        void Add(string? n) { if (!string.IsNullOrWhiteSpace(n) && !list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n.Trim().ToLowerInvariant()); }
        Add(configured);
        try { Add(Dns.GetHostEntry(Environment.MachineName).HostName); } catch { }
        Add(Environment.MachineName);
        Add("localhost");
        return list.ToArray();
    }
}
