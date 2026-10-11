using System.Security.Cryptography;

namespace Zentriq.Application.Licensing;

public sealed record LicensePublicKey(string KeyId, string PublicKeyPem, DateTimeOffset? NotAfter, bool Revoked);

/// <summary>
/// Verification keys trusted by this build. Keys are compiled into the product (embedded resource),
/// never loaded from customer-writable configuration, so a customer cannot add their own signing key.
/// </summary>
public interface ILicensePublicKeyStore
{
    LicensePublicKey? Find(string keyId);
    IReadOnlyList<LicensePublicKey> All { get; }
}

public sealed class InMemoryPublicKeyStore(IEnumerable<LicensePublicKey> keys) : ILicensePublicKeyStore
{
    private readonly Dictionary<string, LicensePublicKey> _keys = keys.ToDictionary(k => k.KeyId, StringComparer.Ordinal);
    public LicensePublicKey? Find(string keyId) => _keys.GetValueOrDefault(keyId);
    public IReadOnlyList<LicensePublicKey> All => _keys.Values.ToList();

    public static ECDsa Import(LicensePublicKey key)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(key.PublicKeyPem);
        if (ecdsa.KeySize != 256) throw new CryptographicException("License verification keys must be ECDSA P-256.");
        return ecdsa;
    }
}
