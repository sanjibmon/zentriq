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
    /// <summary>Printers and multifunction devices (Printer-MIB): page count, supplies, status.</summary>
    [JsonPropertyName("printer")] public PrinterInfo? Printer { get; set; }
    /// <summary>Switches, routers, firewalls: interface counts and roles (SNMP).</summary>
    [JsonPropertyName("ports")] public PortSummary? Ports { get; set; }
    /// <summary>Switches, routers, firewalls, access points: ports, neighbours, VLANs, MAC and ARP tables (SNMP).</summary>
    [JsonPropertyName("networkDevice")] public NetworkDeviceInfo? NetworkDevice { get; set; }
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
    /// <summary>How the device type was recognised (SNMP, web interface, open ports).</summary>
    [JsonPropertyName("identifiedBy")] public List<string>? IdentifiedBy { get; set; }
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
    /// <summary>Edition, e.g. Professional, Enterprise, EnterpriseS (LTSC), ServerStandard; on Linux e.g. "Ubuntu Server", "Fedora Workstation".</summary>
    [JsonPropertyName("editionId")] public string? EditionId { get; set; }
    /// <summary>Linux: distribution id from os-release (ubuntu, debian, rhel, rocky, almalinux, sles, opensuse-leap, fedora, …).</summary>
    [JsonPropertyName("distribution")] public string? Distribution { get; set; }
    /// <summary>Linux: debian | rhel | fedora | suse | arch | alpine | gentoo | other.</summary>
    [JsonPropertyName("distributionFamily")] public string? DistributionFamily { get; set; }
    /// <summary>Linux: installed desktop environment(s) (GNOME, KDE Plasma, Xfce, …), or null on a headless system.</summary>
    [JsonPropertyName("desktopEnvironment")] public string? DesktopEnvironment { get; set; }
    /// <summary>Linux: kernel release (uname -r).</summary>
    [JsonPropertyName("kernel")] public string? Kernel { get; set; }
    /// <summary>Linux: dpkg | rpm | pacman | apk.</summary>
    [JsonPropertyName("packageManager")] public string? PackageManager { get; set; }
    /// <summary>Long-term support release (Ubuntu LTS and similar), when the distribution says so.</summary>
    [JsonPropertyName("lts")] public bool? Lts { get; set; }
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
    /// <summary>Interface description set by the administrator (SNMP ifAlias).</summary>
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    /// <summary>up | down | other (SNMP ifOperStatus).</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }
}

public sealed class PrinterInfo
{
    [JsonPropertyName("pageCount")] public long? PageCount { get; set; }
    /// <summary>idle | printing | warmup | other | unknown (HOST-RESOURCES hrPrinterStatus).</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("supplies")] public List<PrinterSupply> Supplies { get; set; } = new();
}

public sealed class PrinterSupply
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>0-100, or null when the device reports no level.</summary>
    [JsonPropertyName("levelPercent")] public int? LevelPercent { get; set; }
}

public sealed class PortSummary
{
    /// <summary>Physical (Ethernet) interfaces.</summary>
    [JsonPropertyName("physical")] public int Physical { get; set; }
    [JsonPropertyName("up")] public int Up { get; set; }
    [JsonPropertyName("routing")] public bool? Routing { get; set; }
    [JsonPropertyName("switching")] public bool? Switching { get; set; }
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
    /// <summary>Security settings read from the registry and WMI (read-only), assessed by Security Configuration.</summary>
    [JsonPropertyName("hardening")] public HardeningInfo? Hardening { get; set; }
    /// <summary>Linux security settings (read-only), assessed by Security Configuration.</summary>
    [JsonPropertyName("linux")] public LinuxHardeningInfo? Linux { get; set; }
}

/// <summary>Linux security settings. Null = could not be read (not root over SSH, tool missing): reported as "unknown".</summary>
public sealed class LinuxHardeningInfo
{
    /// <summary>Effective PermitRootLogin of the SSH server (yes | no | prohibit-password | forced-commands-only), null without an SSH server.</summary>
    [JsonPropertyName("sshRootLogin")] public string? SshRootLogin { get; set; }
    [JsonPropertyName("sshPasswordAuth")] public bool? SshPasswordAuth { get; set; }
    [JsonPropertyName("sshPort")] public int? SshPort { get; set; }
    /// <summary>The SSH server is installed and running.</summary>
    [JsonPropertyName("sshServer")] public bool? SshServer { get; set; }
    /// <summary>SELinux mode: enforcing | permissive | disabled; null when SELinux is not available.</summary>
    [JsonPropertyName("selinux")] public string? Selinux { get; set; }
    [JsonPropertyName("apparmor")] public bool? AppArmor { get; set; }
    /// <summary>Automatic (security) updates: unattended-upgrades, dnf-automatic / yum-cron, or zypper automatic patches.</summary>
    [JsonPropertyName("autoUpdates")] public bool? AutoUpdates { get; set; }
    [JsonPropertyName("firewall")] public string? Firewall { get; set; }
    [JsonPropertyName("passMaxDays")] public int? PassMaxDays { get; set; }
    /// <summary>Accounts with UID 0 other than root.</summary>
    [JsonPropertyName("extraUid0")] public List<string>? ExtraUid0 { get; set; }
    [JsonPropertyName("auditd")] public bool? Auditd { get; set; }
    [JsonPropertyName("timeSync")] public bool? TimeSync { get; set; }
    [JsonPropertyName("telnetServer")] public bool? TelnetServer { get; set; }
    /// <summary>Package updates waiting to be installed (security ones where the package manager tells).</summary>
    [JsonPropertyName("pendingUpdates")] public int? PendingUpdates { get; set; }
    [JsonPropertyName("pendingSecurityUpdates")] public int? PendingSecurityUpdates { get; set; }
    /// <summary>A reboot is needed to finish installing updates (new kernel, libraries).</summary>
    [JsonPropertyName("rebootRequired")] public bool? RebootRequired { get; set; }
}

/// <summary>
/// Windows security settings. Null = could not be read (older agent, access denied); the console then reports the
/// check as "unknown" rather than passed or failed.
/// </summary>
public sealed class HardeningInfo
{
    [JsonPropertyName("firewallDomain")] public bool? FirewallDomain { get; set; }
    [JsonPropertyName("firewallPrivate")] public bool? FirewallPrivate { get; set; }
    [JsonPropertyName("firewallPublic")] public bool? FirewallPublic { get; set; }
    [JsonPropertyName("uacEnabled")] public bool? UacEnabled { get; set; }
    [JsonPropertyName("uacAdminPrompt")] public int? UacAdminPrompt { get; set; }
    [JsonPropertyName("smb1Enabled")] public bool? Smb1Enabled { get; set; }
    [JsonPropertyName("smbSigningRequired")] public bool? SmbSigningRequired { get; set; }
    [JsonPropertyName("rdpEnabled")] public bool? RdpEnabled { get; set; }
    [JsonPropertyName("rdpNla")] public bool? RdpNla { get; set; }
    [JsonPropertyName("rdpPort")] public int? RdpPort { get; set; }
    [JsonPropertyName("llmnrDisabled")] public bool? LlmnrDisabled { get; set; }
    [JsonPropertyName("autorunDisabled")] public bool? AutorunDisabled { get; set; }
    [JsonPropertyName("wdigestCleartext")] public bool? WDigestCleartext { get; set; }
    [JsonPropertyName("lsaProtection")] public bool? LsaProtection { get; set; }
    [JsonPropertyName("lmCompatibilityLevel")] public int? LmCompatibilityLevel { get; set; }
    [JsonPropertyName("noLmHash")] public bool? NoLmHash { get; set; }
    [JsonPropertyName("tls10Enabled")] public bool? Tls10Enabled { get; set; }
    [JsonPropertyName("tls11Enabled")] public bool? Tls11Enabled { get; set; }
    [JsonPropertyName("ssl3Enabled")] public bool? Ssl3Enabled { get; set; }
    [JsonPropertyName("autoUpdateDisabled")] public bool? AutoUpdateDisabled { get; set; }
    [JsonPropertyName("inactivityTimeoutSecs")] public int? InactivityTimeoutSecs { get; set; }
    [JsonPropertyName("scriptBlockLogging")] public bool? ScriptBlockLogging { get; set; }
    [JsonPropertyName("guestEnabled")] public bool? GuestEnabled { get; set; }
    [JsonPropertyName("builtinAdminEnabled")] public bool? BuiltinAdminEnabled { get; set; }
    [JsonPropertyName("defenderRealtime")] public bool? DefenderRealtime { get; set; }
    [JsonPropertyName("defenderSignatureAgeDays")] public int? DefenderSignatureAgeDays { get; set; }
    [JsonPropertyName("tamperProtection")] public bool? TamperProtection { get; set; }
    [JsonPropertyName("credentialGuard")] public bool? CredentialGuard { get; set; }
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
