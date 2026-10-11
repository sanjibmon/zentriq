using System.Text.Json;
using System.Text.Json.Serialization;
using Zentriq.Application.Licensing;

namespace Zentriq.Infrastructure.Licensing;

/// <summary>Loads the license verification keys compiled into this build.</summary>
public static class EmbeddedPublicKeys
{
    private sealed record KeyFile([property: JsonPropertyName("keys")] List<KeyEntry> Keys);
    private sealed record KeyEntry(
        [property: JsonPropertyName("keyId")] string KeyId,
        [property: JsonPropertyName("publicKeyPem")] string PublicKeyPem,
        [property: JsonPropertyName("notAfter")] DateTimeOffset? NotAfter,
        [property: JsonPropertyName("revoked")] bool Revoked);

    public static ILicensePublicKeyStore Load()
    {
        using var s = typeof(EmbeddedPublicKeys).Assembly.GetManifestResourceStream("Zentriq.Licensing.PublicKeys.json")
                      ?? throw new InvalidOperationException("License public keys are missing from this build.");
        var file = JsonSerializer.Deserialize<KeyFile>(s) ?? throw new InvalidOperationException("License public keys are unreadable.");
        if (file.Keys.Any(k => k.PublicKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal)))
            throw new InvalidOperationException("Refusing to start: a private key was found in the public key file.");
        return new InMemoryPublicKeyStore(file.Keys.Select(k => new LicensePublicKey(k.KeyId, k.PublicKeyPem, k.NotAfter, k.Revoked)));
    }
}
