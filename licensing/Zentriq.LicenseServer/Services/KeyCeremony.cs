using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zentriq.Application.Licensing;
using Zentriq.Licensing.Signing;
using Zentriq.Domain.Licensing;

namespace Zentriq.LicenseServer.Services;

/// <summary>
/// The license signing key: ECDSA P-256 (ES256), generated on the machine that keeps it, stored as an encrypted PKCS#8
/// file (AES-256-CBC, key derived with PBKDF2-HMAC-SHA256, 600,000 iterations), identified by a key id and the SHA-256
/// fingerprint of its public key. The private key never leaves that machine; only the public key goes into Zentriq.
/// </summary>
public static class KeyCeremony
{
    public const string PassphraseEnv = "ZLS_SIGNING_PASSPHRASE";
    public const int MinPassphraseLength = 16;
    public const int Pbkdf2Iterations = 600_000;
    public static readonly PbeParameters Protection = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Pbkdf2Iterations);

    public static string? CheckPassphrase(string? p) =>
        p is null || p.Length < MinPassphraseLength ? $"The signing key passphrase needs at least {MinPassphraseLength} characters." :
        p.Distinct().Count() < 8 ? "The signing key passphrase is too repetitive." : null;

    /// <summary>"SHA256:AB:CD:…" over the DER SubjectPublicKeyInfo — compare it out of band (phone, signed mail) when the public key is handed over.</summary>
    public static string Fingerprint(ECDsa key) => Fingerprint(key.ExportSubjectPublicKeyInfo());
    public static string Fingerprint(byte[] spki) => "SHA256:" + string.Join(':', SHA256.HashData(spki).Select(b => b.ToString("X2")));

    public static string FingerprintOfPem(string publicPem)
    {
        using var k = ECDsa.Create();
        k.ImportFromPem(publicPem);
        return Fingerprint(k);
    }

    public static string? ReadPassphrase(LicenseServerOptions o)
    {
        var p = Environment.GetEnvironmentVariable(PassphraseEnv);
        if (string.IsNullOrEmpty(p) && !string.IsNullOrWhiteSpace(o.SigningKeyPassphraseFile) && File.Exists(o.SigningKeyPassphraseFile))
            p = File.ReadAllText(o.SigningKeyPassphraseFile).TrimEnd('\r', '\n');
        return string.IsNullOrEmpty(p) ? null : p;
    }

    /// <summary>Loads the key. Encrypted keys need the passphrase; unencrypted keys are refused unless explicitly allowed.</summary>
    public static ECDsa Load(string pem, string? passphrase, bool allowUnencrypted)
    {
        var key = ECDsa.Create();
        try
        {
            if (pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(passphrase))
                    throw new InvalidOperationException($"The signing key is encrypted: set {PassphraseEnv} (or LicenseServer:SigningKeyPassphraseFile).");
                try { key.ImportFromEncryptedPem(pem, passphrase); }
                catch (CryptographicException) { throw new InvalidOperationException("The signing key passphrase is not correct, or the key file is damaged."); }
            }
            else
            {
                if (!allowUnencrypted)
                    throw new InvalidOperationException("The signing key file is not encrypted. Create the key with 'keygen' (it encrypts it), or set LicenseServer:AllowUnencryptedKey=true for a test key.");
                key.ImportFromPem(pem);
            }
            if (key.KeySize != 256) throw new InvalidOperationException("The signing key must be ECDSA P-256.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    /// <summary>Signs and verifies a probe with the product's own verifier: the service never starts with a key it cannot use.</summary>
    public static void SelfTest(ECDsa key, string keyId)
    {
        var install = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var probe = new LicensePayload(LicenseFormats.CurrentSchemaVersion, "SELF-TEST", "Self test", install, now, now.AddMinutes(-1), now.AddDays(1), 0, [],
            new Dictionary<string, int> { ["devices"] = 1 }, Edition: "standard", LicenseType: LicenseFormats.Subscription, TermYears: 1, SupportUntil: now.AddDays(1),
            Activation: LicenseFormats.Offline);
        var doc = LicenseSigning.Sign(probe, key, keyId);
        var verifier = new LicenseVerifier(new InMemoryPublicKeyStore([new LicensePublicKey(keyId, key.ExportSubjectPublicKeyInfoPem(), null, false)]));
        var r = verifier.Verify(doc, install, now);
        if (!r.IsValid) throw new InvalidOperationException("Signing key self-test failed: " + r.Message);
    }

    public sealed record Ceremony(string KeyId, string Fingerprint, string PrivateFile, string PublicFile, string EntryFile, string RecordFile);

    /// <summary>Creates a new key in <paramref name="dir"/>: encrypted private key, public key, the entry for Zentriq's key list, and a ceremony record.</summary>
    public static Ceremony Generate(string dir, string keyId, string passphrase)
    {
        if (CheckPassphrase(passphrase) is { } e) throw new InvalidOperationException(e);
        if (!System.Text.RegularExpressions.Regex.IsMatch(keyId, "^[a-z0-9][a-z0-9-]{2,62}$")) throw new InvalidOperationException("Key ids use lower-case letters, digits and hyphens, e.g. zentriq-prod-2026-10.");
        Directory.CreateDirectory(dir);
        var priv = Path.Combine(dir, keyId + ".private.pem");
        if (File.Exists(priv)) throw new InvalidOperationException($"{priv} exists; a signing key is never overwritten.");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var encrypted = key.ExportEncryptedPkcs8PrivateKeyPem(passphrase.AsSpan(), Protection);
        using (var check = Load(encrypted, passphrase, false)) SelfTest(check, keyId);   // round trip before anything is written

        var publicPem = key.ExportSubjectPublicKeyInfoPem();
        var fp = Fingerprint(key);
        WriteOwnerOnly(priv, encrypted);
        var pub = Path.Combine(dir, keyId + ".public.pem");
        File.WriteAllText(pub, publicPem);
        var entryFile = Path.Combine(dir, keyId + ".product-entry.json");
        File.WriteAllText(entryFile, JsonSerializer.Serialize(new { keyId, publicKeyPem = publicPem, notAfter = (string?)null, revoked = false },
            new JsonSerializerOptions { WriteIndented = true }));
        var record = Path.Combine(dir, keyId + ".ceremony.txt");
        File.WriteAllText(record, $"""
            Zentriq license signing key — key ceremony record
            =================================================
            Key id:        {keyId}
            Algorithm:     ECDSA P-256 with SHA-256 (ES256)
            Fingerprint:   {fp}
            Created (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}
            Machine:       {Environment.MachineName}
            Created by:    {Environment.UserName}
            Private key:   {Path.GetFileName(priv)} (encrypted PKCS#8: AES-256-CBC, PBKDF2-HMAC-SHA256, {Pbkdf2Iterations:N0} iterations)

            1. Back up {Path.GetFileName(priv)} to two offline places (e.g. two encrypted USB drives kept apart).
               Store the passphrase separately from the backups (password manager / sealed envelope).
               Without the key and its passphrase no further licenses can be issued with this key id.
            2. Give Zentriq ONLY {Path.GetFileName(entryFile)} (the public key). Confirm the fingerprint above by a
               second channel (phone) when you hand it over; Zentriq builds trust only this key.
            3. Never email, upload or commit the private key. Anyone holding it and its passphrase can issue licenses.
            4. If the key or its passphrase may have leaked: create a new key, release Zentriq with the new public key,
               and mark this key "revoked" (or set "notAfter") in Zentriq's key list.
            """);
        return new(keyId, fp, priv, pub, entryFile, record);
    }

    /// <summary>
    /// Takes over a key created earlier (for example with an earlier edition, or on another machine during a
    /// move): it must be encrypted and open with <paramref name="passphrase"/>; it is self-tested and written like a
    /// new key, with its public key, product entry and a ceremony record.
    /// </summary>
    public static Ceremony Import(string dir, string keyId, string encryptedPem, string passphrase)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(keyId, "^[a-z0-9][a-z0-9-]{2,62}$")) throw new InvalidOperationException("Key ids use lower-case letters, digits and hyphens, e.g. zentriq-prod-2026-10.");
        if (!encryptedPem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal)) throw new InvalidOperationException("Choose the encrypted private key file (<key id>.private.pem).");
        using var key = Load(encryptedPem, passphrase, false);
        SelfTest(key, keyId);
        Directory.CreateDirectory(dir);
        var priv = Path.Combine(dir, keyId + ".private.pem");
        if (File.Exists(priv)) throw new InvalidOperationException($"{priv} exists; a signing key is never overwritten.");
        WriteOwnerOnly(priv, encryptedPem.Trim() + "\n");
        var publicPem = key.ExportSubjectPublicKeyInfoPem();
        var fp = Fingerprint(key);
        var pub = Path.Combine(dir, keyId + ".public.pem");
        File.WriteAllText(pub, publicPem);
        var entryFile = Path.Combine(dir, keyId + ".product-entry.json");
        File.WriteAllText(entryFile, ProductEntry(keyId, publicPem));
        var record = Path.Combine(dir, keyId + ".ceremony.txt");
        File.WriteAllText(record, $"""
            Zentriq license signing key — imported
            ======================================
            Key id:        {keyId}
            Fingerprint:   {fp}
            Imported (UTC): {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} on {Environment.MachineName}
            The key was created earlier; keep its existing offline backups. Zentriq trusts it under this key id.
            """);
        return new(keyId, fp, priv, pub, entryFile, record);
    }

    public static string ProductEntry(string keyId, string publicPem) =>
        JsonSerializer.Serialize(new { keyId, publicKeyPem = publicPem, notAfter = (string?)null, revoked = false }, new JsonSerializerOptions { WriteIndented = true });

    private static void WriteOwnerOnly(string path, string content)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, content);
            var fi = new FileInfo(path);
            var acl = new System.Security.AccessControl.FileSecurity();
            acl.SetAccessRuleProtection(true, false);   // no inherited access
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            fi.SetAccessControl(acl);
        }
        else
        {
            using var fs = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            fs.Write(Encoding.ASCII.GetBytes(content));
        }
    }
}
