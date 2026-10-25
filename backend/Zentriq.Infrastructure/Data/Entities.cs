using System.Net;

namespace Zentriq.Infrastructure.Data;

// Entity classes map 1:1 to tables in database/migrations (snake_case applied by ZentriqDbContext).
// The SQL migrations are authoritative; EF Core is used for data access only (no EF migrations).

public sealed class InstallState
{
    public short Id { get; set; } = 1;
    public Guid InstallationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string ProductVersion { get; set; } = "";
    public byte[]? SetupTokenHash { get; set; }
    public DateTimeOffset? SetupTokenExpiresAt { get; set; }
    public DateTimeOffset? InitializedAt { get; set; }
    public Guid? InitializedBy { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public DateTimeOffset? ClockRollbackDetectedAt { get; set; }
    public DateTimeOffset? UpgradedAt { get; set; }
}

public sealed class InstallationHistory
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Action { get; set; } = "";
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public string Outcome { get; set; } = "";
    public string Detail { get; set; } = "{}";
}

public sealed class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? LegalName { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? StateRegion { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? TaxId { get; set; }
    public string? Website { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Site
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Timezone { get; set; } = "UTC";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Status { get; set; } = "active";
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public bool MfaEnabled { get; set; }
    public string? MfaSecretProtected { get; set; }
    public Guid SecurityStamp { get; set; }
    /// <summary>Changes when the user's roles or their permissions change (sessions then reload their permissions).</summary>
    public Guid AccessStamp { get; set; } = Guid.NewGuid();
    public DateTimeOffset PasswordChangedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<UserRole> Roles { get; set; } = [];
}

public sealed class Permission
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class Role
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool BuiltIn { get; set; }
    public List<RolePermission> Permissions { get; set; } = [];
}

public sealed class RolePermission
{
    public string RoleId { get; set; } = "";
    public string PermissionCode { get; set; } = "";
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public string RoleId { get; set; } = "";
    public Role? Role { get; set; }
}

public sealed class Asset
{
    public Guid Id { get; set; }
    public string? Hostname { get; set; }
    public string? Fqdn { get; set; }
    public IPAddress? PrimaryIp { get; set; }
    public string? SerialNumber { get; set; }
    public string Category { get; set; } = "endpoint";
    public string? OsFamily { get; set; }
    public string? OsName { get; set; }
    public string Status { get; set; } = "active";
    public Guid? MergedInto { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? LocationId { get; set; }
    public string? ManualManufacturer { get; set; }
    public string? ManualModel { get; set; }
    public string? ManualSerial { get; set; }
    public string Criticality { get; set; } = "Medium";
    public string? OwnerName { get; set; }
    public string Source { get; set; } = "manual";
    public string InventoryStatus { get; set; } = "not_collected";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? LastInventoryAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int RowVersion { get; set; } = 1;
    /// <summary>Zentriq.Inventory.DeviceTypes key; <see cref="DeviceTypeManual"/> when an administrator chose it.</summary>
    public string? DeviceType { get; set; }
    public bool DeviceTypeManual { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    /// <summary>JSON array of evidence strings (how the device was recognised without credentials).</summary>
    public string? IdentifiedBy { get; set; }
}

public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public string? SourceIp { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string Outcome { get; set; } = "";
    public string Detail { get; set; } = "{}";
}

public sealed class Job
{
    public Guid Id { get; set; }
    public string JobType { get; set; } = "";
    public string Status { get; set; } = "queued";
    public string Payload { get; set; } = "{}";
    public string? IdempotencyKey { get; set; }
    public int Priority { get; set; } = 100;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 3600;
    public DateTimeOffset RunAfter { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public bool CancelRequested { get; set; }
    public short ProgressPct { get; set; }
    public string? ProgressText { get; set; }
    public string? Result { get; set; }
    public string? LastError { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class JobEvent
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
}

public sealed class WorkerHeartbeat
{
    public string WorkerId { get; set; } = "";
    public string HostName { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastBeatAt { get; set; }
}

public sealed class DiscoveryProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ScanType { get; set; } = "ip";
    public string Config { get; set; } = "{}";
    public string? Schedule { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid? OwnerId { get; set; }
    public bool Shared { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DiscoveryScan
{
    public Guid Id { get; set; }
    public long Number { get; set; }
    public string Name { get; set; } = "";
    public string ScanType { get; set; } = "ip";
    public Guid? ProfileId { get; set; }
    public string Config { get; set; } = "{}";
    public string Status { get; set; } = "queued";
    public int TotalTargets { get; set; }
    public int Processed { get; set; }
    public int AssetsCreated { get; set; }
    public int AssetsUpdated { get; set; }
    public Guid? JobId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class ScanResult
{
    public long Id { get; set; }
    public Guid ScanId { get; set; }
    public string Target { get; set; } = "";
    public IPAddress? IpAddress { get; set; }
    public string? Hostname { get; set; }
    public string Status { get; set; } = "";
    public int[] OpenPorts { get; set; } = [];
    public string? Detail { get; set; }
    public Guid? AssetId { get; set; }
    public bool AssetCreated { get; set; }
    public int? DurationMs { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    /// <summary>Protocol that collected inventory (WinRM, DCOM/WMI, SSH, SNMPv2c, SNMPv3), if any.</summary>
    public string? Protocol { get; set; }
    public Guid? CredentialId { get; set; }
}

public sealed class Dashboard
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? OwnerId { get; set; }
    public bool Shared { get; set; }
    public string? RoleDefault { get; set; }
    public string Filters { get; set; } = "{}";
    public int RefreshSeconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<DashboardWidget> Widgets { get; set; } = [];
}

public sealed class DashboardWidget
{
    public Guid Id { get; set; }
    public Guid DashboardId { get; set; }
    public string WidgetType { get; set; } = "";
    public string? Title { get; set; }
    public int Position { get; set; }
    public short Width { get; set; } = 4;
    public short Height { get; set; } = 1;
    public string Settings { get; set; } = "{}";
}

public sealed class TrialRecord
{
    public short Id { get; set; } = 1;
    public Guid InstallationId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Policy { get; set; } = "{}";
    public string IntegrityMac { get; set; } = "";
}

public sealed class LicenseRecord
{
    public Guid Id { get; set; }
    public string LicenseId { get; set; } = "";
    public int SchemaVersion { get; set; }
    public string KeyId { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public Guid InstallationId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int GraceDays { get; set; }
    public string[] Modules { get; set; } = [];
    public string RawDocument { get; set; } = "";
    public string Status { get; set; } = "active";
    public Guid? ImportedBy { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public List<LicenseEntitlement> Entitlements { get; set; } = [];
}

public sealed class LicenseEntitlement
{
    public Guid LicenseRecordId { get; set; }
    public string Workload { get; set; } = "";
    public int Quantity { get; set; }
}

public sealed class LicenseActivation
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public string RequestDocument { get; set; } = "";
    public string? FulfilledByLicense { get; set; }
}

public sealed class WorkloadUsageSnapshot
{
    public long Id { get; set; }
    public DateTimeOffset TakenAt { get; set; }
    public string Workload { get; set; } = "";
    public int Billable { get; set; }
    public int? Entitled { get; set; }
    public string Source { get; set; } = "";
    public string Details { get; set; } = "{}";
}

public sealed class SystemSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "null";
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}

public sealed class Notification
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string Kind { get; set; } = "info";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Link { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
