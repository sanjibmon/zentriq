using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Zentriq.Agent;

/// <summary>
/// Agent identity: an RSA-2048 key generated on the device and a client certificate issued by the server's
/// agent CA. The private key is imported into LocalMachine\My as non-exportable; only SYSTEM and
/// Administrators can use it. The key never leaves the device (the server only sees the CSR).
/// </summary>
internal sealed class PendingKey : IDisposable
{
    private readonly CngKey _key;
    public RSACng Rsa { get; }

    public PendingKey()
    {
        var p = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowPlaintextExport, KeyUsage = CngKeyUsages.AllUsages };
        p.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
        _key = CngKey.Create(CngAlgorithm.Rsa, null, p); // ephemeral; persisted only via the PFX import below
        Rsa = new RSACng(_key);
    }

    public string CreateCsr(string hostname)
    {
        var req = new CertificateRequest("CN=" + Sanitize(hostname) + ", O=Zentriq Agent", Rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(req.CreateSigningRequest());
    }

    /// <summary>Combines the issued certificate with this key and stores it (non-exportable) in LocalMachine\My.</summary>
    public X509Certificate2 Install(byte[] certificateDer)
    {
        using var issued = new X509Certificate2(certificateDer);
        using var withKey = issued.CopyWithPrivateKey(Rsa);
        var password = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var pfx = withKey.Export(X509ContentType.Pfx, password);
        try
        {
            var stored = new X509Certificate2(pfx, password, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
            stored.FriendlyName = "Zentriq Agent";
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(stored);
            return stored;
        }
        finally { Array.Clear(pfx, 0, pfx.Length); }
    }

    private static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s) if (char.IsLetterOrDigit(c) || c == '-' || c == '.') sb.Append(c);
        return sb.Length == 0 ? "agent" : sb.ToString();
    }

    public void Dispose() { Rsa.Dispose(); _key.Dispose(); }
}

internal static class CertificateStore
{
    public static X509Certificate2? Find(string? thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint)) return null;
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        foreach (var c in found) if (c.HasPrivateKey) return c;
        return null;
    }

    public static void Remove(string? thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint)) return;
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false))
            {
                try
                {
                    // Delete the persisted key container too.
                    if (c.HasPrivateKey && c.GetRSAPrivateKey() is RSACng cng) { store.Remove(c); cng.Key.Delete(); continue; }
                }
                catch (CryptographicException) { }
                store.Remove(c);
            }
        }
        catch (Exception ex) { Log.Warn("Could not remove old agent certificate: " + ex.Message); }
    }
}
