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
    [property: JsonPropertyName("requestId")] Guid? RequestId = null,
    [property: JsonPropertyName("licensee")] Licensee? Licensee = null,
    // "standard" or "advanced" (see Zentriq.Domain.Editions): the edition's modules are added to "modules".
    [property: JsonPropertyName("edition")] string? Edition = null,
    // Schema 3 (license server): "subscription" (1, 3 or 5 years) or "perpetual" (with yearly support).
    [property: JsonPropertyName("licenseType")] string? LicenseType = null,
    [property: JsonPropertyName("termYears")] int? TermYears = null,
    // Perpetual: versions released after this date need renewed support. Subscription: the end of the term.
    [property: JsonPropertyName("supportUntil")] DateTimeOffset? SupportUntil = null,
    // "online" (activated with a license key; refreshed by check-ins) or "offline" (air-gapped, imported file).
    [property: JsonPropertyName("activation")] string? Activation = null,
    // Online activation: the license must be refreshed by a check-in before this time (30 days of grace follow).
    [property: JsonPropertyName("leaseUntil")] DateTimeOffset? LeaseUntil = null);

/// <summary>Customer details carried by schema-2 licenses and shown in the console (signed, so read-only).</summary>
public sealed record Licensee(
    [property: JsonPropertyName("company")] string Company,
    [property: JsonPropertyName("contactName")] string? ContactName = null,
    [property: JsonPropertyName("contactEmail")] string? ContactEmail = null,
    [property: JsonPropertyName("contactPhone")] string? ContactPhone = null,
    [property: JsonPropertyName("address")] string? Address = null,
    [property: JsonPropertyName("country")] string? Country = null,
    [property: JsonPropertyName("taxId")] string? TaxId = null);

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
    /// <summary>2 adds the optional "licensee" block; 3 adds license type, support, activation mode and lease (license server).</summary>
    public const int CurrentSchemaVersion = 3;
    public static readonly IReadOnlySet<int> SupportedSchemaVersions = new HashSet<int> { 1, 2, 3 };
    public const string Subscription = "subscription";
    public const string Perpetual = "perpetual";
    public const string Online = "online";
    public const string Offline = "offline";
    /// <summary>
    /// The license file Zentriq sends to a customer: signed, but bound to no installation. It is only a proof of purchase
    /// for activation; the license server validates it and returns the license bound to the customer's installation.
    /// </summary>
    public const string Certificate = "certificate";
    /// <summary>Days an online license keeps working after its lease ended without a successful check-in.</summary>
    public const int LeaseGraceDays = 30;
    /// <summary>Lease given to an online activation at each successful check-in.</summary>
    public const int LeaseDays = 30;
    /// <summary>Expiry written into perpetual licenses (they do not expire; support does).</summary>
    public static readonly DateTimeOffset PerpetualExpiry = new(2099, 12, 31, 23, 59, 59, TimeSpan.Zero);
    public const int MaxDocumentBytes = 64 * 1024;
}

public sealed record TrialPolicy(int Days, IReadOnlyDictionary<string, int> Limits, IReadOnlyList<string> Modules)
{
    /// <summary>14 days, every module: 30 IT devices, 50 non-IT assets, 10 cloud / virtual instances, 1 remote support technician.</summary>
    public static TrialPolicy Default { get; } = new(14, Plans.TrialLimits, ["*"]);

    /// <summary>Trials stored before the current plan get its new limits (the stored device cap is kept).</summary>
    public TrialPolicy WithCurrentLimits()
    {
        var merged = new Dictionary<string, int>(Plans.TrialLimits);
        foreach (var (k, v) in Limits) if (k == Zentriq.Domain.Workloads.TotalDevices) merged[k] = v;
        return this with { Limits = merged };
    }
}

/// <summary>The commercial plan: trial, free tier, non-IT packs and terms (docs/licensing.md).</summary>
public static class Plans
{
    public static readonly IReadOnlyDictionary<string, int> TrialLimits = new Dictionary<string, int>
    {
        [Zentriq.Domain.Workloads.TotalDevices] = 30, [Zentriq.Domain.Workloads.NonItAssets] = 50,
        [Zentriq.Domain.Workloads.CloudInstances] = 10, [Zentriq.Domain.Workloads.Technicians] = 1,
    };

    /// <summary>Free tier (no license, no expiry): 25 IT devices, 25 non-IT assets, 5 cloud / virtual instances, no remote support.</summary>
    public static readonly IReadOnlyDictionary<string, int> FreeLimits = new Dictionary<string, int>
    {
        [Zentriq.Domain.Workloads.TotalDevices] = 25, [Zentriq.Domain.Workloads.NonItAssets] = 25,
        [Zentriq.Domain.Workloads.CloudInstances] = 5, [Zentriq.Domain.Workloads.Technicians] = 0,
    };

    /// <summary>Free tier modules: the Standard edition (asset management); no remote support or security modules.</summary>
    public static IReadOnlyList<string> FreeModules => Editions.ModulesOf(Editions.Standard);

    /// <summary>Non-IT asset packs; "unlimited" is a license without a non-IT limit.</summary>
    public static readonly IReadOnlyList<int> NonItPacks = [250, 500, 1000, 2000, 4000];
    public static readonly IReadOnlyList<int> SubscriptionYears = [1, 3, 5];
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
    ClockRollback,
    /// <summary>No license and no trial: the free tier (Standard modules, small limits, no expiry).</summary>
    Free,
    /// <summary>Free tier, but the installation holds more than the free limits: features are blocked until it is licensed or reduced.</summary>
    FreeOverLimit,
    /// <summary>Online license not refreshed by a check-in for its lease plus 30 days.</summary>
    LeaseExpired,
    /// <summary>Perpetual license whose support ended before this Zentriq version was released.</summary>
    SupportExpired
}

/// <summary>
/// Protocol between a Zentriq installation and the Zentriq license server (https, JSON). Activation with a license key
/// returns the signed license bound to the installation and a token for later check-ins; check-ins report usage and
/// return a refreshed license (renewals, upgrades, support extensions, a new lease) or the end of the activation.
/// </summary>
public static class LicenseServerProtocol
{
    public const string ActivatePath = "api/v1/activate";
    public const string CheckinPath = "api/v1/checkin";
    public const string DeactivatePath = "api/v1/deactivate";
    public const string OfflineActivatePath = "api/v1/offline/activate";
    /// <summary>Default address of the hosted license server (changeable under Licensing › License server).</summary>
    public const string DefaultServerUrl = "https://license.zentriq.com";
}

/// <summary>Activation with a license key or with the license file (activation "certificate") Zentriq sent to the customer.</summary>
public sealed record OnlineActivateRequest(
    [property: JsonPropertyName("licenseKey")] string? LicenseKey,
    [property: JsonPropertyName("request")] ActivationRequest Request,
    [property: JsonPropertyName("licenseFile")] string? LicenseFile = null);

public sealed record OnlineActivateResponse(
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("activationToken")] string? ActivationToken,
    [property: JsonPropertyName("message")] string Message);

public sealed record OnlineCheckinRequest(
    [property: JsonPropertyName("installationId")] Guid InstallationId,
    [property: JsonPropertyName("licenseId")] string LicenseId,
    [property: JsonPropertyName("productVersion")] string ProductVersion,
    [property: JsonPropertyName("releaseDate")] DateTimeOffset ReleaseDate,
    [property: JsonPropertyName("usage")] IReadOnlyDictionary<string, int> Usage);

/// <param name="Status">"active" (license attached), "suspended", "revoked" or "deactivated".</param>
public sealed record OnlineCheckinResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("license")] string? License,
    [property: JsonPropertyName("message")] string Message);

public sealed record OnlineDeactivateRequest(
    [property: JsonPropertyName("installationId")] Guid InstallationId,
    [property: JsonPropertyName("licenseId")] string LicenseId);

/// <summary>License keys: "ZQL-" + 20 characters (Crockford base 32, grouped by 5).</summary>
public static class LicenseKeys
{
    public const string Prefix = "ZQL";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);
        var chars = bytes.Select(b => Alphabet[b & 31]).ToArray();
        return $"{Prefix}-{new string(chars, 0, 5)}-{new string(chars, 5, 5)}-{new string(chars, 10, 5)}-{new string(chars, 15, 5)}";
    }

    /// <summary>Upper case without spaces; O→0, I/L→1. Null when not a license key.</summary>
    public static string? Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var raw = new string(key.Trim().ToUpperInvariant().Where(ch => ch is not ('-' or ' ')).ToArray());
        // The prefix comes off before the look-alike mapping (its 'L' would otherwise become '1').
        if (raw.StartsWith(Prefix, StringComparison.Ordinal)) raw = raw[Prefix.Length..];
        var t = new string(raw.Select(ch => ch == 'O' ? '0' : ch is 'I' or 'L' ? '1' : ch).ToArray());
        if (t.Length != 20 || t.Any(c => Alphabet.IndexOf(c) < 0)) return null;
        return $"{Prefix}-{t[..5]}-{t[5..10]}-{t[10..15]}-{t[15..]}";
    }

    /// <summary>SHA-256 (hex) of the normalized key: what the license server stores.</summary>
    public static string Hash(string normalized) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("zentriq-license-key\n" + normalized))).ToLowerInvariant();

    /// <summary>"ZQL-AB12C-…-KM78N" for display.</summary>
    public static string Hint(string normalized) => $"{normalized[..9]}…{normalized[^5..]}";
}
