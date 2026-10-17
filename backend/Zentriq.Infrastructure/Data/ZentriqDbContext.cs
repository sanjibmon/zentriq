using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Zentriq.Infrastructure.Data;

public sealed class ZentriqDbContext(DbContextOptions<ZentriqDbContext> options) : DbContext(options)
{
    public DbSet<InstallState> InstallState => Set<InstallState>();
    public DbSet<InstallationHistory> InstallationHistory => Set<InstallationHistory>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobEvent> JobEvents => Set<JobEvent>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<DiscoveryProfile> DiscoveryProfiles => Set<DiscoveryProfile>();
    public DbSet<DiscoveryScan> DiscoveryScans => Set<DiscoveryScan>();
    public DbSet<ScanResult> ScanResults => Set<ScanResult>();
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();
    public DbSet<DashboardWidget> DashboardWidgets => Set<DashboardWidget>();
    public DbSet<TrialRecord> Trial => Set<TrialRecord>();
    public DbSet<LicenseRecord> LicenseRecords => Set<LicenseRecord>();
    public DbSet<LicenseEntitlement> LicenseEntitlements => Set<LicenseEntitlement>();
    public DbSet<LicenseActivation> LicenseActivations => Set<LicenseActivation>();
    public DbSet<WorkloadUsageSnapshot> WorkloadUsageSnapshots => Set<WorkloadUsageSnapshot>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("zentriq");

        b.Entity<InstallState>(e => { e.ToTable("install_state"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever(); });
        b.Entity<InstallationHistory>(e => { e.ToTable("installation_history"); e.Property(x => x.Detail).HasColumnType("jsonb"); });
        b.Entity<Organization>(e => e.ToTable("organizations"));
        b.Entity<Site>(e => e.ToTable("sites"));
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.UserId);
        });
        b.Entity<Permission>(e => { e.ToTable("permissions"); e.HasKey(x => x.Code); });
        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId);
        });
        b.Entity<RolePermission>(e => { e.ToTable("role_permissions"); e.HasKey(x => new { x.RoleId, x.PermissionCode }); });
        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
        });
        b.Entity<Asset>(e =>
        {
            e.ToTable("assets");
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            e.Property(x => x.IdentifiedBy).HasColumnType("jsonb");
        });
        b.Entity<AuditEvent>(e => { e.ToTable("audit_events"); e.Property(x => x.Detail).HasColumnType("jsonb"); });
        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.Result).HasColumnType("jsonb");
        });
        b.Entity<JobEvent>(e => e.ToTable("job_events"));
        b.Entity<WorkerHeartbeat>(e => { e.ToTable("worker_heartbeats"); e.HasKey(x => x.WorkerId); });
        b.Entity<DiscoveryProfile>(e =>
        {
            e.ToTable("discovery_profiles");
            e.Property(x => x.Config).HasColumnType("jsonb");
            e.Property(x => x.Schedule).HasColumnType("jsonb");
        });
        b.Entity<DiscoveryScan>(e =>
        {
            e.ToTable("discovery_scans");
            e.Property(x => x.Config).HasColumnType("jsonb");
            e.Property(x => x.Number).ValueGeneratedOnAdd();
        });
        b.Entity<ScanResult>(e => e.ToTable("scan_results"));
        b.Entity<Dashboard>(e =>
        {
            e.ToTable("dashboards");
            e.Property(x => x.Filters).HasColumnType("jsonb");
            e.HasMany(x => x.Widgets).WithOne().HasForeignKey(x => x.DashboardId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DashboardWidget>(e => { e.ToTable("dashboard_widgets"); e.Property(x => x.Settings).HasColumnType("jsonb"); });
        b.Entity<TrialRecord>(e =>
        {
            e.ToTable("trial");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Policy).HasColumnType("jsonb");
        });
        b.Entity<LicenseRecord>(e =>
        {
            e.ToTable("license_records");
            e.HasMany(x => x.Entitlements).WithOne().HasForeignKey(x => x.LicenseRecordId);
        });
        b.Entity<LicenseEntitlement>(e => { e.ToTable("license_entitlements"); e.HasKey(x => new { x.LicenseRecordId, x.Workload }); });
        b.Entity<LicenseActivation>(e => e.ToTable("license_activations"));
        b.Entity<WorkloadUsageSnapshot>(e => { e.ToTable("workload_usage_snapshots"); e.Property(x => x.Details).HasColumnType("jsonb"); });
        b.Entity<SystemSetting>(e => { e.ToTable("system_settings"); e.HasKey(x => x.Key); e.Property(x => x.Value).HasColumnType("jsonb"); });
        b.Entity<Notification>(e => e.ToTable("notifications"));

        // snake_case column names for every mapped property.
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
    }

    public static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
