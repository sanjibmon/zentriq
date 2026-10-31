using System.Security.Cryptography;
using System.Text.Json;
using Zentriq.Domain.Licensing;

namespace Zentriq.Tests;

/// <summary>Signs license payloads in tests, like the license server does (the product contains no signing code).</summary>
internal static class TestSigning
{
    private static readonly JsonSerializerOptions Compact = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static (string PrivatePem, string PublicPem) NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    public static ECDsa LoadPrivateKey(string pem) { var k = ECDsa.Create(); k.ImportFromPem(pem); return k; }

    public static string Sign(LicensePayload payload, ECDsa key, string keyId)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Compact);
        var sig = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.Serialize(new SignedDocument(LicenseFormats.License, keyId, LicenseFormats.Algorithm, Convert.ToBase64String(bytes), Convert.ToBase64String(sig)));
    }
}
