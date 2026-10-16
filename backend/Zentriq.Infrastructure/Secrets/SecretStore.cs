using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.Infrastructure.Secrets;

/// <summary>Secrets readable by the API and Worker service identities.</summary>
public sealed class AppSecrets
{
    [JsonPropertyName("dbAppPassword")] public string DbAppPassword { get; set; } = "";
    /// <summary>Per-installation HMAC key (base64, 32 bytes) used for trial integrity.</summary>
    [JsonPropertyName("installSecret")] public string InstallSecret { get; set; } = "";
    [JsonPropertyName("httpsPfxPassword")] public string? HttpsPfxPassword { get; set; }
    /// <summary>AES-256 key (base64) protecting discovery credentials in the database.</summary>
    [JsonPropertyName("credentialKey")] public string? CredentialKey { get; set; }
    /// <summary>Password of Secrets\agent-ca.pfx (internal CA that issues agent client certificates).</summary>
    [JsonPropertyName("agentCaPfxPassword")] public string? AgentCaPfxPassword { get; set; }
    /// <summary>Remote support login key (160 hex characters), shared with the ZentriqRemote server's configuration.</summary>
    [JsonPropertyName("remoteLoginKey")] public string? RemoteLoginKey { get; set; }

    public byte[] InstallSecretBytes() => Convert.FromBase64String(InstallSecret);
}

/// <summary>Secrets readable only by SYSTEM and Administrators (provisioning, backup, maintenance).</summary>
public sealed class OwnerSecrets
{
    [JsonPropertyName("dbOwnerPassword")] public string DbOwnerPassword { get; set; } = "";
    /// <summary>Only present when Zentriq installed PostgreSQL itself.</summary>
    [JsonPropertyName("pgSuperPassword")] public string? PgSuperPassword { get; set; }
    [JsonPropertyName("pgSuperUser")] public string? PgSuperUser { get; set; }
    /// <summary>AES-256 key (base64) protecting backup archives. A copy is exported once as the recovery key.</summary>
    [JsonPropertyName("backupKey")] public string BackupKey { get; set; } = "";
}

public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Windows DPAPI, machine scope. File ACLs restrict which identities can read each file.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Zentriq.Secrets.v1");
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);
    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.LocalMachine);
}

/// <summary>
/// Development/test only (non-Windows). AES-GCM with a key from ZENTRIQ_DEV_SECRETS_KEY.
/// Refuses to run unless that variable is set, so it can never be used silently in production.
/// </summary>
public sealed class DevelopmentSecretProtector : ISecretProtector
{
    private readonly byte[] _key;
    public DevelopmentSecretProtector()
    {
        var k = Environment.GetEnvironmentVariable("ZENTRIQ_DEV_SECRETS_KEY")
                ?? throw new InvalidOperationException("Secret protection is unavailable: DPAPI requires Windows and ZENTRIQ_DEV_SECRETS_KEY is not set.");
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(k));
    }

    public byte[] Protect(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, ct, tag);
        return [.. nonce, .. tag, .. ct];
    }

    public byte[] Unprotect(byte[] data)
    {
        var nonce = data.AsSpan(0, 12); var tag = data.AsSpan(12, 16); var ct = data.AsSpan(28);
        var pt = new byte[ct.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, ct, tag, pt);
        return pt;
    }
}

public static class SecretProtectorFactory
{
    public static ISecretProtector Create() =>
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new DevelopmentSecretProtector();
}

public sealed class SecretStore(ISecretProtector protector)
{
    private const string Header = "ZENTRIQ-SECRETS-V1\n";

    public T Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Secrets file not found: {path}. Run 'zentriqctl repair' as an administrator.", path);
        var text = File.ReadAllText(path, Encoding.ASCII);
        if (!text.StartsWith(Header, StringComparison.Ordinal))
            throw new InvalidDataException($"Secrets file {path} has an unknown format.");
        var plain = protector.Unprotect(Convert.FromBase64String(text[Header.Length..].Trim()));
        try { return JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException($"Secrets file {path} is empty."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public T? TryRead<T>(string path) where T : class => File.Exists(path) ? Read<T>(path) : null;

    /// <summary>Writes atomically (temp file + replace). ACLs are applied by the caller (zentriqctl).</summary>
    public void Write<T>(string path, T value) where T : class
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var payload = Header + Convert.ToBase64String(protector.Protect(plain)) + "\n";
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, payload, Encoding.ASCII);
            File.Move(tmp, path, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static string NewPassword(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', 'A').Replace('/', 'z').TrimEnd('=');

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
