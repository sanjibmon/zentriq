namespace Zentriq.Infrastructure.Reports;

public enum ReportColumnType { Text, Number, Date, YesNo }

/// <summary>One selectable report column: a fixed SQL expression (never user input), a label and a type.</summary>
public sealed record ReportColumn(string Key, string Label, ReportColumnType Type, string Expr, string? Group = null);

public sealed record ReportDataset(string Key, string Name, string Description, string From, IReadOnlyList<ReportColumn> Columns)
{
    public ReportColumn? Column(string key) => Columns.FirstOrDefault(c => c.Key == key);
}

/// <summary>
/// Datasets a report can be built on. Reports only choose columns, filters, grouping and sorting from these lists;
/// all SQL comes from here, so a saved report can never run arbitrary SQL.
/// </summary>
public static class ReportCatalog
{
    private const string LastContact = "GREATEST(ag.last_checkin_at, a.last_seen_at)";
    private const string AgentJoin = """
        LEFT JOIN LATERAL (SELECT r.id, r.agent_version, r.last_checkin_at FROM zentriq.agent_registrations r
                            WHERE r.asset_id = a.id AND r.status = 'active' ORDER BY r.last_checkin_at DESC NULLS LAST LIMIT 1) ag ON true
        """;
    private const string DiskGb = "(SELECT round(sum((x ->> 'sizeBytes')::numeric) / 1073741824) FROM jsonb_array_elements(CASE WHEN jsonb_typeof(d.document #> '{hardware,disks}') = 'array' THEN d.document #> '{hardware,disks}' ELSE '[]' END) x)";
    private const string FreePct = "(SELECT round(min((v ->> 'freeBytes')::numeric * 100 / NULLIF((v ->> 'sizeBytes')::numeric, 0)), 1) FROM jsonb_array_elements(CASE WHEN jsonb_typeof(d.document #> '{hardware,volumes}') = 'array' THEN d.document #> '{hardware,volumes}' ELSE '[]' END) v WHERE (v ->> 'sizeBytes')::numeric > 0 AND v ->> 'freeBytes' IS NOT NULL)";
    private static string YesNo(string path) => $"CASE WHEN d.document #>> '{path}' = 'true' THEN 'yes' WHEN d.document #>> '{path}' = 'false' THEN 'no' END";
    private const string TypeExpr = "COALESCE(d.document #>> '{device,chassis}', h.chassis, a.category)";
    private const string OsExpr = "COALESCE(a.os_name, initcap(a.os_family))";

    private static readonly ReportColumn[] DeviceColumns =
    [
        new("hostname", "Hostname", ReportColumnType.Text, "COALESCE(a.hostname, host(a.primary_ip))", "Device"),
        new("fqdn", "FQDN", ReportColumnType.Text, "a.fqdn", "Device"),
        new("ip", "IP address", ReportColumnType.Text, "host(a.primary_ip)", "Device"),
        new("mac", "MAC address", ReportColumnType.Text, "a.mac_address::text", "Device"),
        new("category", "Category", ReportColumnType.Text, "a.category", "Device"),
        new("type", "Device type", ReportColumnType.Text, TypeExpr, "Device"),
        new("os", "Operating system", ReportColumnType.Text, OsExpr, "Device"),
        new("os_version", "OS version", ReportColumnType.Text, "d.document #>> '{os,version}'", "Device"),
        new("domain", "Domain", ReportColumnType.Text, "d.document #>> '{device,domain}'", "Device"),
        new("logged_on_user", "Logged-on user", ReportColumnType.Text, "d.document #>> '{users,loggedOn}'", "Device"),
        new("criticality", "Criticality", ReportColumnType.Text, "a.criticality", "Device"),
        new("owner", "Owner", ReportColumnType.Text, "a.owner_name", "Device"),
    ];

    public static readonly IReadOnlyList<ReportDataset> Datasets =
    [
        new("assets", "Assets", "One row per active device.",
            $"""
             zentriq.assets a
             LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
             LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
             {AgentJoin}
             WHERE a.status = 'active'
             """,
            [
                .. DeviceColumns,
                new("manufacturer", "Manufacturer", ReportColumnType.Text, "h.vendor", "Hardware"),
                new("model", "Model", ReportColumnType.Text, "h.model", "Hardware"),
                new("serial", "Serial number", ReportColumnType.Text, "COALESCE(h.serial_number, a.serial_number)", "Hardware"),
                new("agent", "Agent installed", ReportColumnType.YesNo, "CASE WHEN ag.id IS NOT NULL THEN 'yes' ELSE 'no' END", "Coverage"),
                new("agent_version", "Agent version", ReportColumnType.Text, "ag.agent_version", "Coverage"),
                new("inventory_status", "Inventory status", ReportColumnType.Text, "a.inventory_status", "Coverage"),
                new("inventory_source", "Inventoried by", ReportColumnType.Text, "a.inventory_source", "Coverage"),
                new("first_seen", "First seen", ReportColumnType.Date, "a.first_seen_at", "Coverage"),
                new("last_contact", "Last contact", ReportColumnType.Date, LastContact, "Coverage"),
                new("offline_days", "Days offline", ReportColumnType.Number, $"floor(extract(epoch FROM now() - {LastContact}) / 86400)::int", "Coverage"),
                new("last_inventory", "Last inventory", ReportColumnType.Date, "a.last_inventory_at", "Coverage"),
                new("software_count", "Applications", ReportColumnType.Number, "(SELECT count(*) FROM zentriq.software_inventory s WHERE s.asset_id = a.id)", "Software"),
                new("open_vulns", "Open vulnerabilities", ReportColumnType.Number, "(SELECT count(*) FROM zentriq.vuln_findings f WHERE f.asset_id = a.id AND f.status = 'open')", "Security"),
            ]),

        new("hardware", "Hardware", "One row per device with hardware inventory.",
            """
            zentriq.assets a
            JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
            LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
            WHERE a.status = 'active'
            """,
            [
                .. DeviceColumns,
                new("manufacturer", "Manufacturer", ReportColumnType.Text, "h.vendor", "Hardware"),
                new("model", "Model", ReportColumnType.Text, "h.model", "Hardware"),
                new("serial", "Serial number", ReportColumnType.Text, "COALESCE(h.serial_number, a.serial_number)", "Hardware"),
                new("cpu", "Processor", ReportColumnType.Text, "h.cpu", "Hardware"),
                new("cores", "CPU cores", ReportColumnType.Number, "h.cpu_cores", "Hardware"),
                new("threads", "CPU threads", ReportColumnType.Number, "h.cpu_threads", "Hardware"),
                new("ram_gb", "Memory (GB)", ReportColumnType.Number, "round(h.ram_gb)", "Hardware"),
                new("disk_gb", "Disk total (GB)", ReportColumnType.Number, DiskGb, "Hardware"),
                new("free_pct", "Lowest free space (%)", ReportColumnType.Number, FreePct, "Hardware"),
                new("bios_version", "BIOS version", ReportColumnType.Text, "d.document #>> '{hardware,biosVersion}'", "Hardware"),
                new("virtual", "Virtual machine", ReportColumnType.YesNo, YesNo("{hardware,isVirtual}"), "Hardware"),
                new("tpm", "TPM present", ReportColumnType.YesNo, YesNo("{hardware,tpmPresent}"), "Security"),
                new("secure_boot", "Secure Boot", ReportColumnType.YesNo, YesNo("{security,secureBoot}"), "Security"),
                new("encryption", "Disk encryption", ReportColumnType.YesNo, YesNo("{security,diskEncryption}"), "Security"),
                new("firewall", "Firewall on", ReportColumnType.YesNo, YesNo("{security,firewallEnabled}"), "Security"),
                new("collected", "Collected", ReportColumnType.Date, "h.collected_at", "Hardware"),
            ]),

        new("software", "Software installations", "One row per application installed on a device.",
            """
            zentriq.software_inventory s
            JOIN zentriq.assets a ON a.id = s.asset_id
            LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
            LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
            WHERE a.status = 'active'
            """,
            [
                new("software", "Software", ReportColumnType.Text, "s.name", "Software"),
                new("version", "Version", ReportColumnType.Text, "s.version", "Software"),
                new("publisher", "Publisher", ReportColumnType.Text, "s.publisher", "Software"),
                new("architecture", "Architecture", ReportColumnType.Text, "s.architecture", "Software"),
                new("installed_on", "Installed on", ReportColumnType.Date, "s.installed_on::timestamptz", "Software"),
                new("source", "Reported by", ReportColumnType.Text, "s.source", "Software"),
                .. DeviceColumns,
            ]),

        new("agents", "Agents", "One row per active agent registration.",
            """
            zentriq.agent_registrations r
            LEFT JOIN zentriq.assets a ON a.id = r.asset_id
            WHERE r.status = 'active'
            """,
            [
                new("hostname", "Device", ReportColumnType.Text, "COALESCE(r.hostname, a.hostname)", "Agent"),
                new("os", "Operating system", ReportColumnType.Text, "r.os_name", "Agent"),
                new("agent_version", "Agent version", ReportColumnType.Text, "r.agent_version", "Agent"),
                new("health", "Health", ReportColumnType.Text,
                    "CASE WHEN r.last_checkin_at IS NULL THEN 'never' WHEN r.last_checkin_at > now() - interval '6 minutes' THEN 'online' WHEN r.last_checkin_at > now() - interval '7 days' THEN 'not seen recently' ELSE 'offline' END", "Agent"),
                new("last_checkin", "Last check-in", ReportColumnType.Date, "r.last_checkin_at", "Agent"),
                new("last_inventory", "Last inventory", ReportColumnType.Date, "r.last_inventory_at", "Agent"),
                new("last_ip", "Last address", ReportColumnType.Text, "host(r.last_ip)", "Agent"),
                new("enrolled", "Enrolled", ReportColumnType.Date, "r.enrolled_at", "Agent"),
                new("certificate_expires", "Certificate expires", ReportColumnType.Date, "r.cert_not_after", "Agent"),
            ]),

        new("activity", "Device activity", "Service actions, software uninstalls and agent updates requested in the console.",
            """
            zentriq.asset_actions x
            JOIN zentriq.assets a ON a.id = x.asset_id
            LEFT JOIN zentriq.users u ON u.id = x.requested_by
            """,
            [
                new("requested_at", "Requested", ReportColumnType.Date, "x.requested_at", "Action"),
                new("hostname", "Device", ReportColumnType.Text, "COALESCE(a.hostname, host(a.primary_ip))", "Action"),
                new("action", "Action", ReportColumnType.Text,
                    "CASE x.operation WHEN 'start' THEN 'Start service' WHEN 'stop' THEN 'Stop service' WHEN 'restart' THEN 'Restart service' WHEN 'uninstall' THEN 'Uninstall software' WHEN 'update' THEN 'Update agent' ELSE x.operation END", "Action"),
                new("target", "Target", ReportColumnType.Text, "x.target", "Action"),
                new("channel", "Via", ReportColumnType.Text, "x.channel", "Action"),
                new("status", "Result", ReportColumnType.Text, "x.status", "Action"),
                new("detail", "Detail", ReportColumnType.Text, "x.detail", "Action"),
                new("requested_by", "Requested by", ReportColumnType.Text, "u.display_name", "Action"),
                new("completed_at", "Completed", ReportColumnType.Date, "x.completed_at", "Action"),
            ]),

        new("vulnerabilities", "Vulnerability findings", "One row per vulnerability found on a device.",
            """
            zentriq.vuln_findings f
            JOIN zentriq.vuln_cves c ON c.cve_id = f.cve_id
            JOIN zentriq.assets a ON a.id = f.asset_id
            LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
            LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
            WHERE a.status = 'active'
            """,
            [
                new("cve", "CVE", ReportColumnType.Text, "f.cve_id", "Vulnerability"),
                new("priority", "Priority", ReportColumnType.Text, "f.priority", "Vulnerability"),
                new("severity", "Severity", ReportColumnType.Text, "c.severity", "Vulnerability"),
                new("cvss", "CVSS", ReportColumnType.Number, "c.cvss_score", "Vulnerability"),
                new("epss", "EPSS (%)", ReportColumnType.Number, "round(c.epss * 100, 2)", "Vulnerability"),
                new("kev", "Known exploited (CISA KEV)", ReportColumnType.YesNo, "CASE WHEN c.kev THEN 'yes' ELSE 'no' END", "Vulnerability"),
                new("kev_due", "CISA due date", ReportColumnType.Date, "c.kev_due::timestamptz", "Vulnerability"),
                new("risk", "Risk score", ReportColumnType.Number, "f.risk_score", "Vulnerability"),
                new("description", "Description", ReportColumnType.Text, "left(c.description, 400)", "Vulnerability"),
                new("software", "Software", ReportColumnType.Text, "f.software_name", "Affected"),
                new("version", "Installed version", ReportColumnType.Text, "f.software_version", "Affected"),
                new("fixed_version", "Fixed in", ReportColumnType.Text, "f.fixed_version", "Affected"),
                .. DeviceColumns,
                new("status", "Status", ReportColumnType.Text, "f.status", "Remediation"),
                new("first_seen", "First seen", ReportColumnType.Date, "f.first_seen_at", "Remediation"),
                new("due_at", "Due", ReportColumnType.Date, "f.due_at", "Remediation"),
                new("overdue", "Overdue", ReportColumnType.YesNo, "CASE WHEN f.status = 'open' AND f.due_at < now() THEN 'yes' ELSE 'no' END", "Remediation"),
                new("fixed_at", "Fixed", ReportColumnType.Date, "f.fixed_at", "Remediation"),
                new("exception_reason", "Exception reason", ReportColumnType.Text, "f.exception_reason", "Remediation"),
                new("exception_until", "Exception until", ReportColumnType.Date, "f.exception_until", "Remediation"),
            ]),
    ];

    public static ReportDataset? Find(string key) => Datasets.FirstOrDefault(d => d.Key == key);
}
