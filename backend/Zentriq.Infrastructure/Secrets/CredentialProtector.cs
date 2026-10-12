using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.Infrastructure.Secrets;

/// <summary>Secret part of a discovery credential. Never serialized to API responses or logs.</summary>
public sealed class CredentialSecret
{
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("privateKey")] public string? PrivateKey { get; set; }
    [JsonPropertyName("passphrase")] public string? Passphrase { get; set; }
    [JsonPropertyName("community")] public string? Community { get; set; }
    [JsonPropertyName("authPassword")] public string? AuthPassword { get; set; }
    [JsonPropertyName("privPassword")] public string? PrivPassword { get; set; }
}

/// <summary>
/// AES-256-GCM encryption of credential secrets with the installation credential key (Secrets\app.secrets,
/// DPAPI-protected, readable only by SYSTEM, Administrators and the Zentriq services). The credential id is
/// bound as associated data, so a ciphertext cannot be moved to another credential row.
/// </summary>
public sealed class CredentialProtector(Func<AppSecrets> secrets)
{
    private const string Prefix = "zq-gcm1:";

    private byte[] Key()
    {
        var k = secrets().CredentialKey;
        if (string.IsNullOrEmpty(k))
            throw new InvalidOperationException("The credential key is not provisioned. Run 'zentriqctl repair' on the server.");
        return Convert.FromBase64String(k);
    }

    public string Protect(Guid credentialId, CredentialSecret secret)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(secret);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ct = new byte[plain.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Key(), 16);
            aes.Encrypt(nonce, plain, ct, tag, Encoding.UTF8.GetBytes(credentialId.ToString("D")));
            return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. ct]);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public CredentialSecret Unprotect(Guid credentialId, string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal)) throw new CryptographicException("Unknown credential format.");
        var data = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        var pt = new byte[data.Length - 28];
        using var aes = new AesGcm(Key(), 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), pt, Encoding.UTF8.GetBytes(credentialId.ToString("D")));
        try { return JsonSerializer.Deserialize<CredentialSecret>(pt) ?? new CredentialSecret(); }
        finally { CryptographicOperations.ZeroMemory(pt); }
    }
}
