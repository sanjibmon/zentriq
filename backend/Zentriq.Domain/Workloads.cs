namespace Zentriq.Domain;

/// <summary>
/// Licensed workload categories. An asset is billable when ALL of these hold:
///  1. status = 'active' (retired, duplicate and excluded assets never count),
///  2. it is not merged into another asset (merged_into IS NULL),
///  3. it was seen (discovery, agent or manual confirmation) within the billable window
///     (system setting licensing.billable_seen_within_days, default 30 days).
/// The workload category is derived from assets.category by <see cref="FromAssetCategory"/>.
/// </summary>
public static class Workloads
{
    public const string Endpoint = "endpoint";
    public const string Server = "server";
    public const string NetworkDevice = "network_device";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Endpoint, Server, NetworkDevice, Other];

    /// <summary>
    /// IT devices: every endpoint, server, network device (switch, router, firewall, access point …), printer and other
    /// device counts as one, whatever its type. Virtual machines read from a cloud or virtualization connection count as
    /// <see cref="CloudInstances"/> instead (also when an agent or scan found them).
    /// </summary>
    public const string TotalDevices = "devices";
    /// <summary>Non-IT assets (records that are not disposed), licensed in packs of 250, 500, 1000, 2000, 4000 or unlimited.</summary>
    public const string NonItAssets = "nonit_assets";
    /// <summary>Virtual machines / cloud instances from the cloud and virtualization connections (not deleted).</summary>
    public const string CloudInstances = "cloud_instances";
    /// <summary>Remote support technicians: active users who may start remote sessions.</summary>
    public const string Technicians = "technicians";

    /// <summary>The licensed metrics, in display order. A metric that a license does not list is unlimited.</summary>
    public static readonly IReadOnlyList<string> Metrics = [TotalDevices, NonItAssets, CloudInstances, Technicians];

    public static bool IsLimitKey(string key) => Metrics.Contains(key) || All.Contains(key);

    public static string Title(string key) => key switch
    {
        TotalDevices => "IT devices", NonItAssets => "Non-IT assets", CloudInstances => "Cloud & virtual instances", Technicians => "Remote support technicians",
        Endpoint => "Endpoints", Server => "Servers", NetworkDevice => "Network devices", _ => "Other devices",
    };

    public static string FromAssetCategory(string category) => category switch
    {
        "endpoint" => Endpoint,
        "server" or "virtual_host" => Server,
        "network_device" => NetworkDevice,
        _ => Other
    };

    public static IReadOnlyList<string> AssetCategoriesFor(string workload) => workload switch
    {
        Endpoint => ["endpoint"],
        Server => ["server", "virtual_host"],
        NetworkDevice => ["network_device"],
        _ => ["printer", "iot", "other"]
    };
}

public static class AssetCategories
{
    public static readonly IReadOnlyList<string> All =
        ["endpoint", "server", "network_device", "printer", "iot", "virtual_host", "other"];
}

public static class ScanResultStatus
{
    public const string Unreachable = "unreachable";
    public const string ReachableUnauthenticated = "reachable_unauthenticated";
    public const string AuthFailed = "auth_failed";
    public const string InventoryUnavailable = "inventory_unavailable";
    public const string InventoryFailed = "inventory_failed";
    public const string Success = "success";
    public const string Excluded = "excluded";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";
}

public static class JobTypes
{
    public const string SelfTest = "system.selftest";
    public const string DiscoveryScan = "discovery.scan";
    public const string DirectorySync = "directory.sync";
    public const string AssetAction = "asset.action";
    public const string ReportDeliver = "report.deliver";
    public const string VulnSync = "vuln.sync";
    public const string VulnEvaluate = "vuln.evaluate";
    public const string VulnImport = "vuln.import";
    public const string SecurityEvaluate = "security.evaluate";
    public const string CloudSync = "cloud.sync";
    public const string UsageSnapshot = "license.usage_snapshot";
    public const string MaintenanceBackup = "maintenance.backup";
    /// <summary>Restore from a backup (console: Backup & Restore); runs in the maintenance task, which stops and restarts the services.</summary>
    public const string MaintenanceRestore = "maintenance.restore";
    /// <summary>Export the audit and / or activity log of a period to a zip (Audit Logs › Retention & export).</summary>
    public const string MaintenanceLogArchive = "maintenance.log_archive";

    /// <summary>Jobs executed by the privileged maintenance task (zentriqctl), never by the worker service.</summary>
    public static bool IsMaintenance(string type) => type.StartsWith("maintenance.", StringComparison.Ordinal);
}
