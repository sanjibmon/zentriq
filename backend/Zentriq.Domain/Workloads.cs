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

    /// <summary>Limit key for a total device cap across all workloads (used by the trial policy; also allowed in licenses).</summary>
    public const string TotalDevices = "devices";

    public static bool IsLimitKey(string key) => key == TotalDevices || All.Contains(key);

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
    public const string UsageSnapshot = "license.usage_snapshot";
    public const string MaintenanceBackup = "maintenance.backup";

    /// <summary>Jobs executed by the privileged maintenance task (zentriqctl), never by the worker service.</summary>
    public static bool IsMaintenance(string type) => type.StartsWith("maintenance.", StringComparison.Ordinal);
}
