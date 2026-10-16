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

    public static readonly IReadOnlyList<string> All =
    [
        AssetsView, AssetsManage, ScansRun, ScansProfiles, CredentialsManage, PatchApprove, PatchDeploy,
        DeployRun, RemoteStart, RemoteUnattended, ReportsDesign, ReportsExport, DashboardsShare, VulnExcept,
        JobsView, UsersManage, LicensingManage, BackupRun, SettingsManage, AuditView, AgentsManage, DirectoryManage, AssetsControl, SoftwareUninstall, VulnView, VulnManage,
        SecurityView, SecurityManage, IntegrationsManage
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
