using Npgsql;
using Zentriq.Domain;
using Zentriq.Domain.Licensing;

namespace Zentriq.LicenseServer.Services;

public sealed record Customer(Guid Id, string Company, string? ContactName, string? ContactEmail, string? ContactPhone, string? Address,
    string? Country, string? TaxId, string? Notes, DateTimeOffset CreatedAt)
{
    public const string Columns = "id, company, contact_name, contact_email, contact_phone, address, country, tax_id, notes, created_at";
    public static Customer Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.Str("contact_name"), r.Str("contact_email"),
        r.Str("contact_phone"), r.Str("address"), r.Str("country"), r.Str("tax_id"), r.Str("notes"), r.Time("created_at")!.Value);

    public Licensee ToLicensee() => new(Company, Blank(ContactName), Blank(ContactEmail), Blank(ContactPhone), Blank(Address), Blank(Country), Blank(TaxId));
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed record License(
    string Id, Guid CustomerId, string KeyHint, string Edition, string LicenseType, int? TermYears,
    DateTimeOffset StartsAt, DateTimeOffset ExpiresAt, DateTimeOffset SupportUntil,
    int Devices, int? NonItPack, int CloudInstances, int Technicians, string[] ExtraModules,
    int MaxActivations, int GraceDays, string Status, string? Notes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? FileHash = null, DateTimeOffset? FileIssuedAt = null)
{
    public const string Columns = """
        id, customer_id, key_hint, edition, license_type, term_years, starts_at, expires_at, support_until, devices, nonit_pack,
        cloud_instances, technicians, extra_modules, max_activations, grace_days, status, notes, created_at, updated_at, file_hash, file_issued_at
        """;

    public static License Read(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Int("term_years"),
        r.Time("starts_at")!.Value, r.Time("expires_at")!.Value, r.Time("support_until")!.Value,
        r.GetInt32(r.GetOrdinal("devices")), r.Int("nonit_pack"), r.GetInt32(r.GetOrdinal("cloud_instances")), r.GetInt32(r.GetOrdinal("technicians")),
        r.GetFieldValue<string[]>(r.GetOrdinal("extra_modules")), r.GetInt32(r.GetOrdinal("max_activations")), r.GetInt32(r.GetOrdinal("grace_days")),
        r.GetString(r.GetOrdinal("status")), r.Str("notes"), r.Time("created_at")!.Value, r.Time("updated_at")!.Value, r.Str("file_hash"), r.Time("file_issued_at"));

    public bool IsPerpetual => LicenseType == LicenseFormats.Perpetual;

    /// <summary>Entitlements carried in the license file. Non-IT assets are left out when unlimited; IT accessories are always unlimited.</summary>
    public IReadOnlyDictionary<string, int> Entitlements()
    {
        var e = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            [Workloads.TotalDevices] = Devices,
            [Workloads.CloudInstances] = CloudInstances,
            [Workloads.Technicians] = Technicians,
        };
        if (NonItPack is { } n) e[Workloads.NonItAssets] = n;
        return e;
    }

    /// <summary>Subscription past its end (perpetual licenses never expire; their support does).</summary>
    public bool Expired(DateTimeOffset now) => !IsPerpetual && ExpiresAt < now;
}

public sealed record Activation(
    Guid Id, string LicenseId, Guid InstallationId, string Mode, Guid? RequestId, string? MachineName, string? Organization,
    string? ProductVersion, DateTimeOffset? ReleaseDate, string Usage, string Status, DateTimeOffset ActivatedAt,
    DateTimeOffset? LastCheckinAt, DateTimeOffset? DeactivatedAt, string? DeactivatedBy)
{
    public const string Columns = """
        id, license_id, installation_id, mode, request_id, machine_name, organization, product_version, release_date, usage::text AS usage,
        status, activated_at, last_checkin_at, deactivated_at, deactivated_by
        """;

    public static Activation Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.Uuid("request_id"), r.Str("machine_name"), r.Str("organization"),
        r.Str("product_version"), r.Time("release_date"), r.Str("usage") ?? "{}", r.GetString(r.GetOrdinal("status")), r.Time("activated_at")!.Value,
        r.Time("last_checkin_at"), r.Time("deactivated_at"), r.Str("deactivated_by"));
}

/// <summary>Plans offered for sale (mirrors Zentriq.Domain.Licensing.Plans so the portal and the product agree).</summary>
public static class Catalog
{
    public static IReadOnlyList<int> NonItPacks => Plans.NonItPacks;
    public static IReadOnlyList<int> SubscriptionYears => Plans.SubscriptionYears;
    public static readonly IReadOnlyList<int> SupportYears = [1, 2, 3, 4, 5];
    public static readonly IReadOnlyList<string> Editions = [Domain.Editions.Standard, Domain.Editions.Advanced];
}
