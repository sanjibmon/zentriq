using System.Security.Cryptography;
using System.Text.Json;
using Zentriq.Domain.Licensing;

namespace Zentriq.Application.Licensing;

/// <summary>Signing of license files (vendor side only: the license server and the offline signing tool).</summary>
public static class LicenseSigning
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Compact = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The signed .zlic document (JSON) for <paramref name="payload"/>: ECDSA P-256 / SHA-256 over the exact payload bytes.</summary>
    public static string Sign(LicensePayload payload, ECDsa key, string keyId)
    {
        if (key.KeySize != 256) throw new CryptographicException("The signing key must be ECDSA P-256.");
        // Unset optional fields are left out, so schema-1/2 style licenses stay byte-identical to earlier issuers.
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, Compact);
        var signature = key.SignData(payloadBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var doc = new SignedDocument(LicenseFormats.License, keyId, LicenseFormats.Algorithm, Convert.ToBase64String(payloadBytes), Convert.ToBase64String(signature));
        return JsonSerializer.Serialize(doc, Pretty);
    }

    /// <summary>A new ECDSA P-256 key pair: (private PKCS#8 PEM, public SubjectPublicKeyInfo PEM).</summary>
    public static (string PrivatePem, string PublicPem) NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    public static ECDsa LoadPrivateKey(string pem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        if (key.KeySize != 256) { key.Dispose(); throw new CryptographicException("The signing key must be ECDSA P-256."); }
        return key;
    }
}
