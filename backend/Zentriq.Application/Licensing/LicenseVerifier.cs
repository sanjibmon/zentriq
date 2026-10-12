using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;

namespace Zentriq.Application.Licensing;

public enum LicenseRejection
{
    None,
    TooLarge,
    Malformed,
    UnsupportedFormat,
    UnsupportedAlgorithm,
    UnknownKey,
    KeyRevoked,
    KeyExpired,
    BadSignature,
    UnsupportedSchemaVersion,
    InvalidContent,
    WrongInstallation,
    NotYetValid,
    Expired
}

public sealed record LicenseVerificationResult(LicenseRejection Rejection, string Message, LicensePayload? Payload)
{
    public bool IsValid => Rejection == LicenseRejection.None;
    public static LicenseVerificationResult Fail(LicenseRejection r, string message) => new(r, message, null);
}

/// <summary>
/// Verifies signed offline license files. Order matters: nothing inside the payload is trusted
/// (or even parsed for policy) until the signature over the exact payload bytes has been verified.
/// </summary>
public sealed class LicenseVerifier(ILicensePublicKeyStore keys)
{
    private static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public LicenseVerificationResult Verify(string document, Guid installationId, DateTimeOffset nowUtc, bool enforceValidityWindow = true)
    {
        if (string.IsNullOrWhiteSpace(document))
            return LicenseVerificationResult.Fail(LicenseRejection.Malformed, "The license file is empty.");
        if (Encoding.UTF8.GetByteCount(document) > LicenseFormats.MaxDocumentBytes)
            return LicenseVerificationResult.Fail(LicenseRejection.TooLarge, "The license file is larger than allowed.");

        SignedDocument? envelope;
        try { envelope = JsonSerializer.Deserialize<SignedDocument>(document, Strict); }
        catch (JsonException) { return LicenseVerificationResult.Fail(LicenseRejection.Malformed, "The license file is not valid JSON."); }
        if (envelope is null || envelope.Payload is null || envelope.Signature is null || envelope.KeyId is null)
            return LicenseVerificationResult.Fail(LicenseRejection.Malformed, "The license file is missing required fields.");
        if (envelope.Format != LicenseFormats.License)
            return LicenseVerificationResult.Fail(LicenseRejection.UnsupportedFormat, "This is not a Zentriq license file.");
        if (envelope.Algorithm != LicenseFormats.Algorithm)
            return LicenseVerificationResult.Fail(LicenseRejection.UnsupportedAlgorithm, "Unsupported signature algorithm.");

        var key = keys.Find(envelope.KeyId);
        if (key is null)
            return LicenseVerificationResult.Fail(LicenseRejection.UnknownKey, $"The license was signed with an unknown key ({envelope.KeyId}).");
        if (key.Revoked)
            return LicenseVerificationResult.Fail(LicenseRejection.KeyRevoked, "The signing key for this license has been revoked.");

        byte[] payloadBytes, signature;
        try
        {
            payloadBytes = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException) { return LicenseVerificationResult.Fail(LicenseRejection.Malformed, "The license encoding is invalid."); }

        bool signatureOk;
        try
        {
            using var ecdsa = InMemoryPublicKeyStore.Import(key);
            signatureOk = ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException) { signatureOk = false; }
        if (!signatureOk)
            return LicenseVerificationResult.Fail(LicenseRejection.BadSignature, "The license signature is invalid. The file may have been modified.");

        // Signature verified: the payload is authentic. Now validate its content.
        LicensePayload? payload;
        try { payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes, Strict); }
        catch (JsonException) { return LicenseVerificationResult.Fail(LicenseRejection.InvalidContent, "The license content is not valid."); }
        if (payload is null)
            return LicenseVerificationResult.Fail(LicenseRejection.InvalidContent, "The license content is empty.");
        if (!LicenseFormats.SupportedSchemaVersions.Contains(payload.SchemaVersion))
            return LicenseVerificationResult.Fail(LicenseRejection.UnsupportedSchemaVersion, $"License schema version {payload.SchemaVersion} is not supported by this Zentriq version.");

        var contentError = ValidateContent(payload);
        if (contentError is not null)
            return LicenseVerificationResult.Fail(LicenseRejection.InvalidContent, contentError);

        if (key.NotAfter is { } keyNotAfter && payload.IssuedAt > keyNotAfter)
            return LicenseVerificationResult.Fail(LicenseRejection.KeyExpired, "The license was issued after its signing key was retired.");
        if (payload.InstallationId != installationId)
            return LicenseVerificationResult.Fail(LicenseRejection.WrongInstallation, "This license was issued for a different Zentriq installation.");

        if (enforceValidityWindow)
        {
            if (nowUtc < payload.NotBefore)
                return LicenseVerificationResult.Fail(LicenseRejection.NotYetValid, "This license is not valid yet.");
            if (nowUtc >= payload.ExpiresAt.AddDays(payload.GraceDays))
                return LicenseVerificationResult.Fail(LicenseRejection.Expired, "This license has expired.");
        }

        return new LicenseVerificationResult(LicenseRejection.None, "License is valid.", payload);
    }

    private static string? ValidateContent(LicensePayload p)
    {
        if (string.IsNullOrWhiteSpace(p.LicenseId) || p.LicenseId.Length > 64) return "License ID is missing or too long.";
        if (string.IsNullOrWhiteSpace(p.Customer) || p.Customer.Length > 200) return "Customer name is missing or too long.";
        if (p.InstallationId == Guid.Empty) return "Installation binding is missing.";
        if (p.ExpiresAt <= p.NotBefore) return "License validity window is empty.";
        if (p.IssuedAt > p.ExpiresAt) return "License issue date is after its expiry.";
        if (p.GraceDays is < 0 or > 60) return "Grace period is out of range.";
        if (p.Modules is null || p.Modules.Count == 0) return "License lists no modules.";
        foreach (var m in p.Modules)
            if (m != "*" && !Modules.All.Contains(m)) return $"Unknown module '{m}'.";
        if (p.Entitlements is null || p.Entitlements.Count == 0) return "License lists no workload entitlements.";
        foreach (var (workload, qty) in p.Entitlements)
        {
            if (!Workloads.IsLimitKey(workload)) return $"Unknown workload category '{workload}'.";
            if (qty is < 0 or > 10_000_000) return $"Entitlement for '{workload}' is out of range.";
        }
        if (p.SchemaVersion < 2 && p.Licensee is not null) return "Licensee details require license schema 2.";
        if (p.Licensee is { } l && (string.IsNullOrWhiteSpace(l.Company) || l.Company.Length > 200)) return "Licensee company is missing or too long.";
        return null;
    }
}
