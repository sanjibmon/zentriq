using System.Security.Cryptography;
using Npgsql;
using Zentriq.Application.Licensing;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;

namespace Zentriq.LicenseServer.Services;

public sealed class LicenseServerOptions
{
    /// <summary>PEM file holding the ECDSA P-256 private signing key (PKCS#8). Keep it readable by the service account only.</summary>
    public string? SigningKeyFile { get; set; }
    /// <summary>Key id carried in license files; the product must list the matching public key under the same id.</summary>
    public string SigningKeyId { get; set; } = "zentriq-prod-2026";
    /// <summary>Public base address shown on the offline activation page.</summary>
    public string PublicUrl { get; set; } = LicenseServerProtocol.DefaultServerUrl;
    public int LeaseDays { get; set; } = LicenseFormats.LeaseDays;
    /// <summary>File holding the signing key passphrase (Docker secret). Otherwise the ZLS_SIGNING_PASSPHRASE environment variable.</summary>
    public string? SigningKeyPassphraseFile { get; set; }
    /// <summary>Accept an unencrypted PEM key (tests only).</summary>
    public bool AllowUnencryptedKey { get; set; }
}

/// <summary>The private signing key, loaded once and self-tested. Missing, unreadable or wrong keys stop the service at start-up.</summary>
public sealed class SigningKey : IDisposable
{
    public ECDsa Key { get; }
    public string KeyId { get; }
    public string PublicPem { get; }
    public string Fingerprint { get; }

    public SigningKey(ECDsa key, string keyId)
    {
        Key = key; KeyId = keyId; PublicPem = key.ExportSubjectPublicKeyInfoPem(); Fingerprint = KeyCeremony.Fingerprint(key);
    }

    public static SigningKey Load(LicenseServerOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.SigningKeyFile))
            throw new InvalidOperationException("LicenseServer:SigningKeyFile is not set. Create a key with 'Zentriq.LicenseServer keygen --out <dir>'.");
        if (!File.Exists(o.SigningKeyFile)) throw new InvalidOperationException($"The signing key file {o.SigningKeyFile} does not exist.");
        var key = KeyCeremony.Load(File.ReadAllText(o.SigningKeyFile), KeyCeremony.ReadPassphrase(o), o.AllowUnencryptedKey);
        KeyCeremony.SelfTest(key, o.SigningKeyId);
        return new SigningKey(key, o.SigningKeyId);
    }

    public void Dispose() => Key.Dispose();
}

/// <summary>Builds and signs license files for activations, and records every issued file.</summary>
public sealed class LicenseIssuer(SigningKey key, LicenseServerOptions options, TimeProvider time)
{
    public DateTimeOffset Now => time.GetUtcNow();

    public LicensePayload Payload(License l, Customer c, Guid installationId, Guid? requestId, string activation, DateTimeOffset now)
    {
        // Online activations carry a lease: the product needs a check-in before it ends (30 more days of grace follow).
        DateTimeOffset? lease = activation == LicenseFormats.Online ? now.AddDays(options.LeaseDays) : null;
        if (lease is { } le && !l.IsPerpetual && le > l.ExpiresAt.AddDays(l.GraceDays)) lease = l.ExpiresAt.AddDays(l.GraceDays);
        if (lease is { } le2 && le2 < now) lease = now;
        return new LicensePayload(
            SchemaVersion: LicenseFormats.CurrentSchemaVersion,
            LicenseId: l.Id,
            Customer: c.Company,
            InstallationId: installationId,
            IssuedAt: now,
            NotBefore: l.StartsAt < now ? l.StartsAt : now.AddMinutes(-5),
            ExpiresAt: l.IsPerpetual ? LicenseFormats.PerpetualExpiry : l.ExpiresAt,
            GraceDays: l.GraceDays,
            Modules: l.ExtraModules.Where(m => Modules.All.Contains(m)).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList(),
            Entitlements: l.Entitlements(),
            RequestId: requestId,
            Licensee: c.ToLicensee(),
            Edition: l.Edition,
            LicenseType: l.LicenseType,
            TermYears: l.IsPerpetual ? null : l.TermYears,
            SupportUntil: l.SupportUntil,
            Activation: activation,
            LeaseUntil: lease);
    }

    public async Task<(string Document, LicensePayload Payload)> IssueAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, License l, Customer c,
        Guid installationId, Guid? requestId, Guid? activationId, string activation, string reason, CancellationToken ct)
    {
        var now = Now;
        var payload = Payload(l, c, installationId, requestId, activation, now);
        var doc = LicenseSigning.Sign(payload, key.Key, key.KeyId);
        await conn.ExecAsync("""
            INSERT INTO zls.issued_documents (id, license_id, activation_id, issued_at, lease_until, reason, document)
            VALUES (@id, @lic, @act, @at, @lease, @reason, @doc)
            """, new { id = Guid.NewGuid(), lic = l.Id, act = (object?)activationId, at = now, lease = (object?)payload.LeaseUntil, reason, doc }, ct, tx);
        return (doc, payload);
    }

    /// <summary>"ZQ-2026-7K3QF9": year of issue and six random characters (Crockford base 32).</summary>
    public string NewLicenseId()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var b = RandomNumberGenerator.GetBytes(6);
        return $"ZQ-{Now.Year}-{new string(b.Select(x => alphabet[x & 31]).ToArray())}";
    }
}

public static class Tokens
{
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("zentriq-activation-token\n" + token))).ToLowerInvariant();
}
