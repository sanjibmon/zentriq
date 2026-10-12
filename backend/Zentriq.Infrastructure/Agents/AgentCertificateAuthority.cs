using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Zentriq.Infrastructure.Agents;

/// <summary>
/// Installation-private CA that issues Zentriq Agent client certificates (mutual TLS on the agent port).
/// It is created by zentriqctl (Secrets\agent-ca.pfx, password in app.secrets) and is never used for
/// anything except agent identities; it is not added to any Windows trust store.
/// </summary>
public static class AgentCertificateAuthority
{
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    public static readonly TimeSpan ClientLifetime = TimeSpan.FromDays(365);

    /// <summary>Creates the CA if it does not exist (or cannot be opened). Returns true if a new CA was created.</summary>
    public static bool Ensure(string pfxPath, string password, string installationName)
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                using var existing = Load(pfxPath, password);
                if (existing.NotAfter > DateTime.UtcNow.AddYears(1)) return false;
            }
            catch (CryptographicException) { }
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var req = new CertificateRequest($"CN=Zentriq Agent CA ({Sanitize(installationName)}), O=Zentriq", key, HashAlgorithmName.SHA384);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(15));
        var tmp = pfxPath + ".tmp";
        File.WriteAllBytes(tmp, ca.Export(X509ContentType.Pfx, password));
        File.Move(tmp, pfxPath, overwrite: true);
        return true;
    }

    public static X509Certificate2 Load(string pfxPath, string password) =>
        X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password, X509KeyStorageFlags.EphemeralKeySet);

    private static string Sanitize(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '.' or ' ').Take(60).ToArray());

    /// <summary>
    /// Issues a client certificate for <paramref name="agentId"/> from a PKCS#10 request. Only the public key
    /// is taken from the request; subject, extensions and lifetime are fixed by the server.
    /// </summary>
    public static X509Certificate2 Issue(X509Certificate2 ca, byte[] csrDer, Guid agentId)
    {
        CertificateRequest csr;
        try
        {
            csr = CertificateRequest.LoadSigningRequest(csrDer, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException ex) { throw new ArgumentException("The certificate request is not valid: " + ex.Message, nameof(csrDer)); }
        ValidateKey(csr.PublicKey);

        var req = new CertificateRequest($"CN={agentId:D}, O=Zentriq Agent", csr.PublicKey, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ClientAuthOid)], true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-10);
        var notAfter = notBefore + ClientLifetime;
        if (notAfter > ca.NotAfter) notAfter = ca.NotAfter.AddMinutes(-1);
        using var caKey = ca.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Agent CA private key is unavailable.");
        var gen = X509SignatureGenerator.CreateForECDsa(caKey);
        return req.Create(ca.SubjectName, gen, notBefore, notAfter, serial);
    }

    private static void ValidateKey(PublicKey pk)
    {
        using var ec = pk.GetECDsaPublicKey();
        if (ec is not null)
        {
            if (ec.KeySize < 256) throw new ArgumentException("ECDSA keys must be at least P-256.");
            return;
        }
        using var rsa = pk.GetRSAPublicKey();
        if (rsa is not null)
        {
            if (rsa.KeySize < 2048) throw new ArgumentException("RSA keys must be at least 2048 bits.");
            return;
        }
        throw new ArgumentException("Unsupported key type (use ECDSA P-256 or RSA 2048+).");
    }

    /// <summary>Validates a presented client certificate: chains to this CA, client-auth EKU, time-valid. Returns the agent id.</summary>
    public static Guid? Validate(X509Certificate2 ca, X509Certificate2 client, out string? error)
    {
        error = null;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // revocation is enforced by the database (status/thumbprint)
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthOid));
        chain.ChainPolicy.VerificationTimeIgnored = false;
        if (!chain.Build(client))
        {
            error = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0));
            return null;
        }
        if (chain.ChainElements.Count != 2 || chain.ChainElements[^1].Certificate.Thumbprint != ca.Thumbprint)
        {
            error = "Certificate was not issued by this server's agent CA.";
            return null;
        }
        var cn = client.GetNameInfo(X509NameType.SimpleName, false);
        if (!Guid.TryParse(cn, out var id)) { error = "Certificate subject is not an agent id."; return null; }
        return id;
    }

    /// <summary>SHA-256 over the SubjectPublicKeyInfo (base64) — what agents pin for the server certificate.</summary>
    public static string SpkiSha256(X509Certificate2 cert) =>
        Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

    public static string SerialHex(X509Certificate2 cert) => cert.SerialNumber;
}
