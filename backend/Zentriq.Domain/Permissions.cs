namespace Zentriq.Domain;

/// <summary>Permission codes. Must match database/migrations reference data.</summary>
public static class Permissions
{
    public const string AssetsView = "assets.view";
    public const string AssetsManage = "assets.manage";
    public const string ScansRun = "scans.run";
    public const string ScansProfiles = "scans.profiles";
    public const string CredentialsManage = "credentials.manage";
    public const string PatchApprove = "patch.approve";
    public const string PatchDeploy = "patch.deploy";
    public const string DeployRun = "deploy.run";
    public const string RemoteStart = "remote.start";
    public const string RemoteUnattended = "remote.unattended";
    public const string ReportsDesign = "reports.design";
    public const string ReportsExport = "reports.export";
    public const string DashboardsShare = "dashboards.share";
    public const string VulnExcept = "vuln.except";
    public const string JobsView = "jobs.view";
    public const string UsersManage = "users.manage";
    public const string LicensingManage = "licensing.manage";
    public const string BackupRun = "backup.run";
    public const string SettingsManage = "settings.manage";
    public const string AuditView = "audit.view";
    public const string AgentsManage = "agents.manage";
    public const string DirectoryManage = "directory.manage";
    public const string AssetsControl = "assets.control";
    public const string SoftwareUninstall = "software.uninstall";
    public const string VulnView = "vuln.view";
    public const string VulnManage = "vuln.manage";
    public const string SecurityView = "security.view";
    public const string SecurityManage = "security.manage";
    public const string IntegrationsManage = "integrations.manage";
    public const string RemoteManage = "remote.manage";
    public const string DashboardView = "dashboard.view";
    public const string ReportsView = "reports.view";
    public const string ActivityView = "activity.view";
    public const string NonItView = "nonit.view";
    public const string NonItManage = "nonit.manage";
    public const string AccessoriesView = "accessories.view";
    public const string AccessoriesManage = "accessories.manage";
    public const string LifecycleView = "lifecycle.view";
    public const string LifecycleManage = "lifecycle.manage";
    public const string CloudView = "cloud.view";
    public const string CloudManage = "cloud.manage";

    /// <summary>Policy: may see at least one asset area (departments and locations are shared by all of them).</summary>
    public const string AnyAssetView = "any:assets.view";
    /// <summary>Policy: may change at least one asset area.</summary>
    public const string AnyAssetManage = "any:assets.manage";
    public static readonly IReadOnlyList<string> AssetViews = [AssetsView, NonItView, AccessoriesView, LifecycleView, CloudView];
    public static readonly IReadOnlyList<string> AssetManages = [AssetsManage, NonItManage, AccessoriesManage, LifecycleManage, CloudManage];

    public static readonly IReadOnlyList<string> All =
    [
        AssetsView, AssetsManage, ScansRun, ScansProfiles, CredentialsManage, PatchApprove, PatchDeploy,
        DeployRun, RemoteStart, RemoteUnattended, ReportsDesign, ReportsExport, DashboardsShare, VulnExcept,
        JobsView, UsersManage, LicensingManage, BackupRun, SettingsManage, AuditView, AgentsManage, DirectoryManage, AssetsControl, SoftwareUninstall, VulnView, VulnManage,
        SecurityView, SecurityManage, IntegrationsManage, RemoteManage, DashboardView, ReportsView, ActivityView, NonItView, NonItManage,
        AccessoriesView, AccessoriesManage, LifecycleView, LifecycleManage, CloudView, CloudManage
    ];
}

/// <summary>Product modules gated by licensing.</summary>
public static class Modules
{
    public const string Inventory = "inventory";
    public const string Discovery = "discovery";
    public const string Dashboards = "dashboards";
    public const string Reports = "reports";
    public const string Patch = "patch";
    public const string Vulnerability = "vulnerability";
    public const string Configuration = "configuration";
    public const string Deployment = "deployment";
    public const string Remote = "remote";
    public const string Cloud = "cloud";
    public const string Lifecycle = "lifecycle";
    public const string Integrations = "integrations";

    public static readonly IReadOnlyList<string> All =
    [
        Inventory, Discovery, Dashboards, Reports, Patch, Vulnerability, Configuration, Deployment, Remote, Cloud,
        Lifecycle, Integrations
    ];
}

/// <summary>
/// The product sections (the console's navigation groups and the installer's choices) and the modules in each.
/// Standard installation and Standard edition: Overview, Discover &amp; inventory, Infrastructure (Cloud &amp; infrastructure,
/// Asset lifecycle) and Insight &amp; admin. Advanced adds Remote support and Secure &amp; remediate. What can be used is what is
/// installed AND licensed (the trial licenses everything).
/// </summary>
public static class ProductSections
{
    public const string Overview = "overview";
    public const string Discover = "discover";
    public const string Remote = "remote";
    public const string Infrastructure = "infrastructure";
    public const string Secure = "secure";
    public const string Insight = "insight";

    public static readonly IReadOnlyList<string> All = [Overview, Discover, Remote, Infrastructure, Secure, Insight];
    public static readonly IReadOnlyList<string> Standard = [Overview, Discover, Infrastructure, Insight];
    public static readonly IReadOnlyList<string> Optional = [Remote, Secure];

    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        [Overview] = "Overview", [Discover] = "Discover & inventory", [Remote] = "Remote support",
        [Infrastructure] = "Infrastructure", [Secure] = "Secure & remediate", [Insight] = "Insight & admin",
    };

    public static readonly IReadOnlyDictionary<string, string[]> ModulesOf = new Dictionary<string, string[]>
    {
        [Overview] = [Modules.Dashboards],
        [Discover] = [Modules.Inventory, Modules.Discovery],
        [Remote] = [Modules.Remote],
        [Infrastructure] = [Modules.Cloud, Modules.Lifecycle],
        [Secure] = [Modules.Patch, Modules.Vulnerability, Modules.Configuration, Modules.Deployment],
        [Insight] = [Modules.Reports, Modules.Integrations],
    };

    public static string? SectionOf(string module) => ModulesOf.FirstOrDefault(kv => kv.Value.Contains(module)).Key;

    /// <summary>
    /// Installed sections from a stored list ("overview,discover,..." or a JSON array): unknown names dropped, the standard
    /// sections always included. Null or empty means everything (installations from before sections existed).
    /// </summary>
    public static IReadOnlyList<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return All;
        var names = value.Split([',', ';', ' ', '[', ']', '"'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).ToHashSet();
        if (names.Contains("all") || names.Contains("advanced")) return All;
        return All.Where(s => Standard.Contains(s) || names.Contains(s)).ToList();
    }

    /// <summary>The modules of these sections.</summary>
    public static IReadOnlyList<string> ModulesFor(IEnumerable<string> sections) => sections.SelectMany(s => ModulesOf.TryGetValue(s, out var m) ? m : []).Distinct().ToList();
}

/// <summary>Commercial editions. A license names its edition; the edition's modules are added to any modules it lists.</summary>
public static class Editions
{
    public const string Standard = "standard";
    public const string Advanced = "advanced";

    public static IReadOnlyList<string> ModulesOf(string? edition) => edition?.Trim().ToLowerInvariant() switch
    {
        Standard => ProductSections.ModulesFor(ProductSections.Standard),
        Advanced => ProductSections.ModulesFor(ProductSections.All),
        _ => [],
    };

    /// <summary>Effective modules of a license: its edition's modules plus the modules it lists ("*" = all).</summary>
    public static IReadOnlyList<string> Expand(string? edition, IReadOnlyList<string> modules) =>
        modules.Contains("*") ? ["*"] : ModulesOf(edition).Concat(modules).Distinct().ToList();

    public const string Free = "free";

    public static string Title(string? edition) => edition?.Trim().ToLowerInvariant() switch { Standard => "Standard", Advanced => "Advanced", Free => "Free", "trial" => "Trial", _ => "Custom" };
}
