using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Zentriq.Infrastructure.Certificates;
using Zentriq.Infrastructure.Configuration;

namespace Zentriq.Setup;

/// <summary>
/// HTTPS certificate handling. Default: an RSA-3072 server certificate issued by the installation's local CA, with
/// SANs for the configured host name, the machine's FQDN/NetBIOS name, localhost and the server's IPv4 addresses,
/// written as a password-protected PFX in the Secrets folder; the CA is trusted in LocalMachine\Root on this server.
/// Alternatively an existing certificate from LocalMachine\My (by thumbprint).
/// </summary>
public sealed class CertificateManager(Reporter log)
{
    public sealed record CertificateInfo(string Thumbprint, DateTime NotAfter, bool SelfSigned, string Subject);

    /// <summary>
    /// The installation's own HTTPS identity: a local CA (Secrets\https-ca.pfx, created once, trusted in LocalMachine\Root
    /// on this server and to be trusted once on technician PCs) issues the console certificate for the server's names and
    /// IP addresses. The API re-issues it by itself when they change (ConsoleCertificateRenewer). An existing certificate
    /// key is kept so agents, which pin it, keep connecting.
    /// </summary>
    public CertificateInfo EnsureSelfSigned(ZentriqPaths paths, string hostName, string pfxPassword, string caPassword, bool forceNew)
    {
        X509Certificate2? ca = null;
        if (File.Exists(paths.HttpsCaFile))
        {
            try { ca = ServerCertificates.Load(paths.HttpsCaFile, caPassword); }
            catch (CryptographicException) { log.Warn("The local certificate authority could not be opened; creating a new one."); }
        }
        if (ca is null || ca.NotAfter < DateTime.Now.AddYears(1))
        {
            ca?.Dispose();
            using var created = ServerCertificates.CreateCa(Environment.MachineName);
            ServerCertificates.Save(created, paths.HttpsCaFile, caPassword);
            ca = ServerCertificates.Load(paths.HttpsCaFile, caPassword);
            log.Info($"Created the local certificate authority \"{ca.GetNameInfo(X509NameType.SimpleName, false)}\" (Secrets\\https-ca.pfx).");
        }
        using (ca)
        {
            Trust(ca);
            RemoveOldSelfSigned();
            var names = ServerCertificates.Names(hostName);
            var addresses = ServerCertificates.Addresses(hostName);
            RSA? key = null;
            X509Certificate2? existing = null;
            if (File.Exists(paths.HttpsCertificateFile))
            {
                try
                {
                    existing = ServerCertificates.Load(paths.HttpsCertificateFile, pfxPassword);
                    if (!forceNew && ServerCertificates.Covers(existing, ca, names, addresses))
                    {
                        log.Info($"Keeping HTTPS certificate {existing.Thumbprint} (expires {existing.NotAfter:yyyy-MM-dd}).");
                        using (existing) return new(existing.Thumbprint, existing.NotAfter, true, existing.Subject);
                    }
                    if (!forceNew) key = existing.GetRSAPrivateKey(); // same key: agents pin it
                }
                catch (CryptographicException) { log.Warn("Existing HTTPS certificate could not be opened; issuing a new one with a new key."); }
            }
            try
            {
                using var cert = ServerCertificates.Issue(ca, key, names, addresses);
                ServerCertificates.Save(cert, paths.HttpsCertificateFile, pfxPassword);
                log.Info($"Issued HTTPS certificate {cert.Thumbprint} from the local CA for {string.Join(", ", names.Concat(addresses.Select(a => a.ToString())))}{(key is null ? "" : " (same key)")}.");
                return new(cert.Thumbprint, cert.NotAfter, true, cert.Subject);
            }
            finally { key?.Dispose(); existing?.Dispose(); }
        }
    }

    /// <summary>Builds before 53 trusted their self-signed console certificate directly; the local CA replaces it.</summary>
    private void RemoveOldSelfSigned()
    {
        try
        {
            using var root = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            root.Open(OpenFlags.ReadWrite);
            foreach (var c in root.Certificates.Where(c => c.Subject.Contains("O=Zentriq (self-signed)", StringComparison.Ordinal)).ToList())
            {
                root.Remove(c);
                log.Info($"Removed the previous self-signed console certificate {c.Thumbprint} from the trusted root store.");
            }
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException) { log.Warn("Could not clean up the trusted root store: " + ex.Message); }
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
                log.Info($"Trusted {cert.GetNameInfo(X509NameType.SimpleName, false)} on this server (LocalMachine\\Root). Install it once on technician PCs (Zentriq console: Remote Support › Deployment & settings › Trust this server), or use a CA-issued certificate.");
            }
        }
        catch (Exception ex) { log.Warn($"Could not add the certificate to the trusted root store: {ex.Message}"); }
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
