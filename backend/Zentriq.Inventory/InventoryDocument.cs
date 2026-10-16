using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Zentriq.Inventory;

/// <summary>
/// Normalized inventory of one device, produced by the Zentriq Agent or by an agentless collector
/// (WinRM/WMI, SSH, SNMP). Uploaded/stored as JSON; unknown values are null, never guessed.
/// </summary>
public sealed class InventoryDocument
{
    public const int CurrentSchema = 1;
    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    /// <summary>agent | winrm | ssh | snmp</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "agent";
    [JsonPropertyName("collectedAt")] public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("collectorVersion")] public string? CollectorVersion { get; set; }
    [JsonPropertyName("device")] public DeviceInfo Device { get; set; } = new();
    [JsonPropertyName("os")] public OsInfo Os { get; set; } = new();
    [JsonPropertyName("hardware")] public HardwareInfo Hardware { get; set; } = new();
    [JsonPropertyName("network")] public List<NetworkAdapter> Network { get; set; } = new();
    [JsonPropertyName("software")] public List<SoftwareItem> Software { get; set; } = new();
    [JsonPropertyName("patches")] public List<PatchItem> Patches { get; set; } = new();
    [JsonPropertyName("services")] public List<ServiceItem> Services { get; set; } = new();
    [JsonPropertyName("security")] public SecurityInfo Security { get; set; } = new();
    [JsonPropertyName("users")] public UserInfo Users { get; set; } = new();
    /// <summary>Sections that could not be collected, with the reason (no secrets).</summary>
    [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();
}

public sealed class DeviceInfo
{
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonPropertyName("fqdn")] public string? Fqdn { get; set; }
    [JsonPropertyName("domain")] public string? Domain { get; set; }
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }
    [JsonPropertyName("smbiosUuid")] public string? SmbiosUuid { get; set; }
    /// <summary>desktop | laptop | server | virtual | network | printer | other</summary>
    [JsonPropertyName("chassis")] public string? Chassis { get; set; }
    /// <summary>Zentriq asset category derived from the data (endpoint, server, virtual_host, network_device, printer, other).</summary>
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("contact")] public string? Contact { get; set; }
}

public sealed class OsInfo
{
    /// <summary>windows | linux | macos | network | other</summary>
    [JsonPropertyName("family")] public string? Family { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("build")] public string? Build { get; set; }
    [JsonPropertyName("architecture")] public string? Architecture { get; set; }
    [JsonPropertyName("installDate")] public DateTimeOffset? InstallDate { get; set; }
    [JsonPropertyName("lastBoot")] public DateTimeOffset? LastBoot { get; set; }
    [JsonPropertyName("isServer")] public bool? IsServer { get; set; }
    /// <summary>Update Build Revision: the monthly patch level (10.0.17763.<b>6893</b>). Needed to match Windows vulnerabilities.</summary>
    [JsonPropertyName("ubr")] public int? Ubr { get; set; }
    /// <summary>Feature update, e.g. 22H2 / 23H2 (DisplayVersion, or ReleaseId on older builds).</summary>
    [JsonPropertyName("displayVersion")] public string? DisplayVersion { get; set; }
    /// <summary>Client, Server or Server Core.</summary>
    [JsonPropertyName("installationType")] public string? InstallationType { get; set; }
}

public sealed class HardwareInfo
{
    [JsonPropertyName("cpuName")] public string? CpuName { get; set; }
    [JsonPropertyName("cpuCores")] public int? CpuCores { get; set; }
    [JsonPropertyName("cpuThreads")] public int? CpuThreads { get; set; }
    [JsonPropertyName("memoryBytes")] public long? MemoryBytes { get; set; }
    [JsonPropertyName("biosVendor")] public string? BiosVendor { get; set; }
    [JsonPropertyName("biosVersion")] public string? BiosVersion { get; set; }
    [JsonPropertyName("biosDate")] public DateTimeOffset? BiosDate { get; set; }
    [JsonPropertyName("tpmPresent")] public bool? TpmPresent { get; set; }
    [JsonPropertyName("tpmVersion")] public string? TpmVersion { get; set; }
    [JsonPropertyName("isVirtual")] public bool? IsVirtual { get; set; }
    [JsonPropertyName("disks")] public List<Disk> Disks { get; set; } = new();
    [JsonPropertyName("volumes")] public List<Volume> Volumes { get; set; } = new();
}

public sealed class Disk
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("serial")] public string? Serial { get; set; }
    [JsonPropertyName("sizeBytes")] public long? SizeBytes { get; set; }
    [JsonPropertyName("mediaType")] public string? MediaType { get; set; }
    [JsonPropertyName("interface")] public string? Interface { get; set; }
}

public sealed class Volume
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("fileSystem")] public string? FileSystem { get; set; }
    [JsonPropertyName("sizeBytes")] public long? SizeBytes { get; set; }
    [JsonPropertyName("freeBytes")] public long? FreeBytes { get; set; }
    [JsonPropertyName("encrypted")] public bool? Encrypted { get; set; }
}

public sealed class NetworkAdapter
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("mac")] public string? Mac { get; set; }
    [JsonPropertyName("ipv4")] public List<string> Ipv4 { get; set; } = new();
    [JsonPropertyName("ipv6")] public List<string> Ipv6 { get; set; } = new();
    [JsonPropertyName("gateway")] public string? Gateway { get; set; }
    [JsonPropertyName("dhcp")] public bool? Dhcp { get; set; }
    [JsonPropertyName("speedMbps")] public long? SpeedMbps { get; set; }
}

public sealed class SoftwareItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }
    [JsonPropertyName("installDate")] public DateTimeOffset? InstallDate { get; set; }
    [JsonPropertyName("architecture")] public string? Architecture { get; set; }
    /// <summary>registry | appx | dpkg | rpm | brew | other</summary>
    [JsonPropertyName("source")] public string? Source { get; set; }
}

public sealed class PatchItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("installedOn")] public DateTimeOffset? InstalledOn { get; set; }
}

public sealed class ServiceItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("startMode")] public string? StartMode { get; set; }
}

public sealed class SecurityInfo
{
    [JsonPropertyName("antivirus")] public List<AntivirusProduct> Antivirus { get; set; } = new();
    [JsonPropertyName("firewallEnabled")] public bool? FirewallEnabled { get; set; }
    [JsonPropertyName("diskEncryption")] public bool? DiskEncryption { get; set; }
    [JsonPropertyName("secureBoot")] public bool? SecureBoot { get; set; }
}

public sealed class AntivirusProduct
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("upToDate")] public bool? UpToDate { get; set; }
}

public sealed class UserInfo
{
    [JsonPropertyName("loggedOn")] public string? LoggedOn { get; set; }
    [JsonPropertyName("localAdmins")] public List<string> LocalAdmins { get; set; } = new();
}
