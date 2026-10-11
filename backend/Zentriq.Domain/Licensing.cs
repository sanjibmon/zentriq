using System.Text.Json.Serialization;

namespace Zentriq.Domain.Licensing;

/// <summary>Outer envelope of a signed license file (.zlic). The payload is signed as raw bytes.</summary>
public sealed record SignedDocument(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("payload")] string Payload,     // base64 of UTF-8 JSON
    [property: JsonPropertyName("signature")] string Signature); // base64 ECDSA P-256 / SHA-256, IEEE P1363

public sealed record LicensePayload(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("licenseId")] string LicenseId,
    [property: JsonPropertyName("customer")] string Customer,
    [property: JsonPropertyName("installationId")] Guid InstallationId,
    [property: JsonPropertyName("issuedAt")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("notBefore")] DateTimeOffset NotBefore,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("graceDays")] int GraceDays,
    [property: JsonPropertyName("modules")] IReadOnlyList<string> Modules,
    [property: JsonPropertyName("entitlements")] IReadOnlyDictionary<string, int> Entitlements,
    [property: JsonPropertyName("requestId")] Guid? RequestId = null);

/// <summary>Offline activation request exported by the customer and sent to the vendor.</summary>
public sealed record ActivationRequest(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("installationId")] Guid InstallationId,
    [property: JsonPropertyName("productVersion")] string ProductVersion,
    [property: JsonPropertyName("machineName")] string MachineName,
    [property: JsonPropertyName("organization")] string Organization,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("currentUsage")] IReadOnlyDictionary<string, int> CurrentUsage);

public static class LicenseFormats
{
    public const string License = "zentriq-license";
    public const string ActivationRequest = "zentriq-activation-request";
    public const string Algorithm = "ES256";
    public const int CurrentSchemaVersion = 1;
    public static readonly IReadOnlySet<int> SupportedSchemaVersions = new HashSet<int> { 1 };
    public const int MaxDocumentBytes = 64 * 1024;
}

public sealed record TrialPolicy(int Days, IReadOnlyDictionary<string, int> Limits, IReadOnlyList<string> Modules)
{
    public static TrialPolicy Default { get; } = new(
        14,
        new Dictionary<string, int> { ["endpoint"] = 250, ["server"] = 25, ["network_device"] = 50, ["other"] = 50 },
        ["*"]);
}

public enum LicenseMode
{
    /// <summary>Setup has not completed; no trial has started.</summary>
    Uninitialized,
    Trial,
    TrialExpired,
    Licensed,
    /// <summary>Commercial license past expiry but inside its grace period.</summary>
    LicensedGrace,
    LicenseExpired,
    /// <summary>System clock moved backwards beyond tolerance; feature APIs are restricted until corrected.</summary>
    ClockRollback
}
