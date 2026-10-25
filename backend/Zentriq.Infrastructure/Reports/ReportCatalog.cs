namespace Zentriq.Infrastructure.Reports;

public enum ReportColumnType { Text, Number, Date, YesNo }

/// <summary>One selectable report column: a fixed SQL expression (never user input), a label and a type.</summary>
public sealed record ReportColumn(string Key, string Label, ReportColumnType Type, string Expr, string? Group = null);

/// <summary>A data source. <paramref name="Permission"/>: extra permission needed to use it (besides viewing assets).</summary>
public sealed record ReportDataset(string Key, string Name, string Description, string From, IReadOnlyList<ReportColumn> Columns, string? Permission = null)
{
    public ReportColumn? Column(string key) => Columns.FirstOrDefault(c => c.Key == key);
}

/// <summary>
/// Datasets a report can be built on. Reports only choose columns, filters, grouping and sorting from these lists;
/// all SQL comes from here, so a saved report can never run arbitrary SQL.
/// </summary>
public static class ReportCatalog
{
    /// <summary>
    /// The permission a data source needs: its own (vulnerabilities, security, audit), else the permission of the area its
    /// rows come from (non-IT assets, IT accessories, lifecycle, cloud, device activity), else the IT asset inventory.
    /// </summary>
    public static string RequiredPermission(ReportDataset d) => d.Permission ?? d.Key switch
    {
        "nonit_assets" => "nonit.view",
        "accessory_stock" or "accessory_movements" => "accessories.view",
        "asset_lifecycle" or "asset_lifecycle_events" => "lifecycle.view",
        "cloud_resources" or "cloud_findings" => "cloud.view",
        "activity" => "activity.view",
        _ => "assets.view",
    };

    private const string LastContact = "GREATEST(ag.last_checkin_at, a.last_seen_at)";
    private const string AgentJoin = """
        LEFT JOIN LATERAL (SELECT r.id, r.agent_version, r.last_checkin_at FROM zentriq.agent_registrations r
                            WHERE r.asset_id = a.id AND r.status = 'active' ORDER BY r.last_checkin_at DESC NULLS LAST LIMIT 1) ag ON true
        """;
    private const string DiskGb = "(SELECT round(sum((x ->> 'sizeBytes')::numeric) / 1073741824) FROM jsonb_array_elements(CASE WHEN jsonb_typeof(d.document #> '{hardware,disks}') = 'array' THEN d.document #> '{hardware,disks}' ELSE '[]' END) x)";
    private const string FreePct = "(SELECT round(min((v ->> 'freeBytes')::numeric * 100 / NULLIF((v ->> 'sizeBytes')::numeric, 0)), 1) FROM jsonb_array_elements(CASE WHEN jsonb_typeof(d.document #> '{hardware,volumes}') = 'array' THEN d.document #> '{hardware,volumes}' ELSE '[]' END) v WHERE (v ->> 'sizeBytes')::numeric > 0 AND v ->> 'freeBytes' IS NOT NULL)";
    private static string YesNo(string path) => $"CASE WHEN d.document #>> '{path}' = 'true' THEN 'yes' WHEN d.document #>> '{path}' = 'false' THEN 'no' END";
    private const string TypeExpr = "COALESCE(a.device_type, d.document #>> '{device,chassis}', h.chassis, a.category)";
    /// <summary>The device's IPv6 address (primary when IPv6, else the first global address of its adapters); reports show IPv4 by default.</summary>
    private const string Ipv6Expr = "COALESCE(CASE WHEN family(a.primary_ip) = 6 THEN host(a.primary_ip) END, (SELECT ip FROM jsonb_array_elements(COALESCE(d.document->'network', '[]'::jsonb)) nic, jsonb_array_elements_text(COALESCE(nic->'ipv6', '[]'::jsonb)) ip WHERE ip NOT ILIKE 'fe80%' AND ip <> '::1' LIMIT 1))";
    private const string StageExpr = "CASE v.stage WHEN 'planned' THEN 'Planned' WHEN 'on_order' THEN 'On order' WHEN 'in_stock' THEN 'In stock' WHEN 'in_use' THEN 'In use' WHEN 'in_maintenance' THEN 'In maintenance' WHEN 'missing' THEN 'Missing' WHEN 'retired' THEN 'Retired' WHEN 'disposed' THEN 'Disposed' END";
    private const string OsExpr = "COALESCE(a.os_name, initcap(a.os_family))";
    private static string CloudProviderLabel(string col) =>
        $"CASE {col} {string.Join(" ", Zentriq.Infrastructure.Cloud.CloudProviders.All.Select(p => $"WHEN '{p}' THEN '{Zentriq.Infrastructure.Cloud.CloudProviders.Title(p)}'"))} ELSE {col} END";
    private static string CloudKindLabel(string col) =>
        $"CASE {col} {string.Join(" ", Zentriq.Infrastructure.Cloud.CloudKinds.Labels.Select(k => $"WHEN '{k.Key}' THEN '{k.Value}'"))} ELSE {col} END";

    private static readonly ReportColumn[] DeviceColumns =
    [
        new("hostname", "Hostname", ReportColumnType.Text, "COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip))", "Device"),
        new("fqdn", "FQDN", ReportColumnType.Text, "a.fqdn", "Device"),
        new("ip", "IP address", ReportColumnType.Text, "COALESCE(zentriq.asset_ipv4(a.id), host(a.primary_ip))", "Device"),
        new("ipv6", "IPv6 address", ReportColumnType.Text, Ipv6Expr, "Device"),
        new("mac", "MAC address", ReportColumnType.Text, "a.mac_address::text", "Device"),
        new("category", "Category", ReportColumnType.Text, "a.category", "Device"),
        new("type", "Device type", ReportColumnType.Text, TypeExpr, "Device"),
        new("os", "Operating system", ReportColumnType.Text, OsExpr, "Device"),
        new("os_version", "OS version", ReportColumnType.Text, "d.document #>> '{os,version}'", "Device"),
        new("domain", "Domain", ReportColumnType.Text, "d.document #>> '{device,domain}'", "Device"),
        new("logged_on_user", "Logged-on user", ReportColumnType.Text, "d.document #>> '{users,loggedOn}'", "Device"),
        new("criticality", "Criticality", ReportColumnType.Text, "a.criticality", "Device"),
        new("owner", "Owner", ReportColumnType.Text, "a.owner_name", "Device"),
        new("department", "Department / unit", ReportColumnType.Text, "(SELECT dp.name FROM zentriq.departments dp WHERE dp.id = a.department_id)", "Device"),
        new("hw_entered", "Hardware details entered manually", ReportColumnType.YesNo, "CASE WHEN a.manual_manufacturer IS NOT NULL OR a.manual_model IS NOT NULL OR a.manual_serial IS NOT NULL THEN 'yes' ELSE 'no' END", "Device"),
        new("asset_location", "Location (site / office)", ReportColumnType.Text, "(SELECT lo.name FROM zentriq.locations lo WHERE lo.id = a.location_id)", "Device"),
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
                new("manufacturer", "Manufacturer", ReportColumnType.Text, "COALESCE(zentriq.hw_value(h.vendor), zentriq.hw_value(a.manufacturer), a.manual_manufacturer)", "Hardware"),
                new("model", "Model", ReportColumnType.Text, "COALESCE(zentriq.hw_value(h.model), zentriq.hw_value(a.model), a.manual_model)", "Hardware"),
                new("serial", "Serial number", ReportColumnType.Text, "COALESCE(zentriq.hw_value(h.serial_number), zentriq.hw_value(a.serial_number), a.manual_serial)", "Hardware"),
                new("agent", "Agent installed", ReportColumnType.YesNo, "CASE WHEN ag.id IS NOT NULL THEN 'yes' ELSE 'no' END", "Coverage"),
                new("agent_version", "Agent version", ReportColumnType.Text, "ag.agent_version", "Coverage"),
                new("inventory_status", "Inventory status", ReportColumnType.Text, "a.inventory_status", "Coverage"),
                new("inventory_source", "Inventoried by", ReportColumnType.Text, "a.inventory_source", "Coverage"),
                new("first_seen", "First seen", ReportColumnType.Date, "a.first_seen_at", "Coverage"),
                new("last_contact", "Last contact", ReportColumnType.Date, LastContact, "Coverage"),
                new("offline_days", "Days offline", ReportColumnType.Number, $"floor(extract(epoch FROM now() - {LastContact}) / 86400)::int", "Coverage"),
                new("last_inventory", "Last inventory", ReportColumnType.Date, "a.last_inventory_at", "Coverage"),
                new("software_count", "Applications", ReportColumnType.Number, "(SELECT count(*) FROM zentriq.software_inventory s WHERE s.asset_id = a.id)", "Software"),
                new("open_vulns", "Open vulnerabilities", ReportColumnType.Number, "(SELECT count(*) FROM zentriq.vuln_findings f WHERE f.asset_id = a.id AND f.status IN ('open','in_progress'))", "Security"),
                new("critical_vulns", "Critical vulnerabilities", ReportColumnType.Number, "(SELECT count(*) FROM zentriq.vuln_findings f JOIN zentriq.vuln_cves c ON c.cve_id = f.cve_id WHERE f.asset_id = a.id AND f.status IN ('open','in_progress') AND c.severity = 'critical')", "Security"),
                new("vuln_risk", "Vulnerability risk (sum)", ReportColumnType.Number, "(SELECT COALESCE(sum(f.risk_score), 0) FROM zentriq.vuln_findings f WHERE f.asset_id = a.id AND f.status IN ('open','in_progress'))", "Security"),
                new("os_build", "OS build", ReportColumnType.Text, "(d.document #>> '{os,version}') || COALESCE('.' || (d.document #>> '{os,ubr}'), '')", "Device"),
                new("feature_update", "Feature update", ReportColumnType.Text, "d.document #>> '{os,displayVersion}'", "Device"),
                new("last_boot", "Last boot", ReportColumnType.Date, "(d.document #>> '{os,lastBoot}')::timestamptz", "Device"),
                new("security_score", "Security score", ReportColumnType.Number, "(SELECT min(sc.score) FROM zentriq.sec_scores sc WHERE sc.asset_id = a.id)", "Security"),
                new("security_failed", "Failed security checks", ReportColumnType.Number, "(SELECT count(*) FROM zentriq.sec_results sr WHERE sr.asset_id = a.id AND sr.status = 'fail')", "Security"),
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
                new("manufacturer", "Manufacturer", ReportColumnType.Text, "COALESCE(zentriq.hw_value(h.vendor), zentriq.hw_value(a.manufacturer), a.manual_manufacturer)", "Hardware"),
                new("model", "Model", ReportColumnType.Text, "COALESCE(zentriq.hw_value(h.model), zentriq.hw_value(a.model), a.manual_model)", "Hardware"),
                new("serial", "Serial number", ReportColumnType.Text, "COALESCE(zentriq.hw_value(h.serial_number), zentriq.hw_value(a.serial_number), a.manual_serial)", "Hardware"),
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
                    "CASE WHEN r.last_checkin_at IS NULL THEN 'never' WHEN r.online_until > now() THEN 'online' WHEN r.last_checkin_at > now() - interval '7 days' THEN 'not seen recently' ELSE 'offline' END", "Agent"),
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
                new("hostname", "Device", ReportColumnType.Text, "COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip))", "Action"),
                new("action", "Action", ReportColumnType.Text,
                    "CASE x.operation WHEN 'start' THEN 'Start service' WHEN 'stop' THEN 'Stop service' WHEN 'restart' THEN 'Restart service' WHEN 'uninstall' THEN CASE WHEN x.kind = 'remote-support' THEN 'Remove remote support' ELSE 'Uninstall software' END WHEN 'update' THEN 'Update agent' WHEN 'install' THEN 'Install remote support' ELSE x.operation END", "Action"),
                new("target", "Target", ReportColumnType.Text, "x.target", "Action"),
                new("channel", "Via", ReportColumnType.Text, "x.channel", "Action"),
                new("status", "Result", ReportColumnType.Text, "x.status", "Action"),
                new("detail", "Detail", ReportColumnType.Text, "x.detail", "Action"),
                new("requested_by", "Requested by", ReportColumnType.Text, "u.display_name", "Action"),
                new("completed_at", "Completed", ReportColumnType.Date, "x.completed_at", "Action"),
            ]),

        new("vulnerabilities", "Vulnerability findings", "One row per vulnerability found on a device (software and Windows).",
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
                new("title", "Title", ReportColumnType.Text, "c.title", "Vulnerability"),
                new("source", "Found in", ReportColumnType.Text, "CASE f.source WHEN 'os' THEN 'os' ELSE 'software' END", "Vulnerability"),
                new("exploited", "Known exploited", ReportColumnType.YesNo, "CASE WHEN c.kev OR c.ms_exploited THEN 'yes' ELSE 'no' END", "Vulnerability"),
                new("ms_severity", "Microsoft severity", ReportColumnType.Text, "c.ms_severity", "Vulnerability"),
                new("priority", "Priority", ReportColumnType.Text, "f.priority", "Vulnerability"),
                new("severity", "Severity", ReportColumnType.Text, "c.severity", "Vulnerability"),
                new("cvss", "CVSS", ReportColumnType.Number, "c.cvss_score", "Vulnerability"),
                new("epss", "EPSS (%)", ReportColumnType.Number, "round(c.epss * 100, 2)", "Vulnerability"),
                new("kev", "Known exploited (CISA KEV)", ReportColumnType.YesNo, "CASE WHEN c.kev THEN 'yes' ELSE 'no' END", "Vulnerability"),
                new("kev_due", "CISA due date", ReportColumnType.Date, "c.kev_due::timestamptz", "Vulnerability"),
                new("risk", "Risk score", ReportColumnType.Number, "f.risk_score", "Vulnerability"),
                new("description", "Description", ReportColumnType.Text, "left(c.description, 400)", "Vulnerability"),
                new("software", "Software", ReportColumnType.Text, "f.software_name", "Affected"),
                new("version", "Installed version", ReportColumnType.Text, "COALESCE(f.installed_version, f.software_version)", "Affected"),
                new("installed_version", "Installed build", ReportColumnType.Text, "f.installed_version", "Affected"),
                new("fixed_version", "Fixed in", ReportColumnType.Text, "f.fixed_version", "Affected"),
                new("kb", "Windows update (KB)", ReportColumnType.Text, "CASE WHEN f.kb IS NOT NULL THEN 'KB' || f.kb END", "Affected"),
                .. DeviceColumns,
                new("status", "Status", ReportColumnType.Text, "f.status", "Remediation"),
                new("assignee", "Assigned to", ReportColumnType.Text, "(SELECT u.display_name FROM zentriq.users u WHERE u.id = f.assignee_id)", "Remediation"),
                new("ticket", "Ticket", ReportColumnType.Text, "f.ticket", "Remediation"),
                new("first_seen", "First seen", ReportColumnType.Date, "f.first_seen_at", "Remediation"),
                new("age_days", "Age (days)", ReportColumnType.Number, "floor(extract(epoch FROM COALESCE(f.fixed_at, now()) - f.first_seen_at) / 86400)::int", "Remediation"),
                new("age_bucket", "Age", ReportColumnType.Text,
                    "CASE WHEN now() - f.first_seen_at <= interval '30 days' THEN '0–30 days' WHEN now() - f.first_seen_at <= interval '60 days' THEN '31–60 days' WHEN now() - f.first_seen_at <= interval '90 days' THEN '61–90 days' WHEN now() - f.first_seen_at <= interval '180 days' THEN '91–180 days' ELSE 'Over 180 days' END", "Remediation"),
                new("due_at", "Due", ReportColumnType.Date, "f.due_at", "Remediation"),
                new("overdue", "Overdue", ReportColumnType.YesNo, "CASE WHEN f.status IN ('open','in_progress') AND f.due_at < now() THEN 'yes' ELSE 'no' END", "Remediation"),
                new("fixed_at", "Fixed", ReportColumnType.Date, "f.fixed_at", "Remediation"),
                new("exception_reason", "Exception reason", ReportColumnType.Text, "f.exception_reason", "Remediation"),
                new("exception_until", "Exception until", ReportColumnType.Date, "f.exception_until", "Remediation"),
            ], Permission: "vuln.view"),

        new("security", "Security configuration", "One row per configuration check per device and baseline (Windows).",
            """
            zentriq.sec_results s
            JOIN zentriq.assets a ON a.id = s.asset_id
            JOIN zentriq.sec_baselines b ON b.id = s.baseline_id
            LEFT JOIN zentriq.sec_rules r ON r.id = s.rule_id
            LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
            LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
            WHERE a.status = 'active'
            """,
            [
                new("rule", "Check", ReportColumnType.Text, "s.rule_id", "Check"),
                new("title", "Title", ReportColumnType.Text, "r.title", "Check"),
                new("check_category", "Check area", ReportColumnType.Text, "r.category", "Check"),
                new("severity", "Severity", ReportColumnType.Text, "s.severity", "Check"),
                new("severity_rank", "Severity order", ReportColumnType.Number, "CASE s.severity WHEN 'high' THEN 1 WHEN 'medium' THEN 2 ELSE 3 END", "Check"),
                new("reference", "Reference", ReportColumnType.Text, "r.reference", "Check"),
                new("remediation", "Remediation", ReportColumnType.Text, "r.remediation", "Check"),
                new("status", "Result", ReportColumnType.Text, "s.status", "Result"),
                new("actual", "Value found", ReportColumnType.Text, "s.actual", "Result"),
                new("first_failed", "Failing since", ReportColumnType.Date, "s.first_failed_at", "Result"),
                new("failing_days", "Days failing", ReportColumnType.Number, "floor(extract(epoch FROM now() - s.first_failed_at) / 86400)::int", "Result"),
                new("evaluated", "Checked at", ReportColumnType.Date, "s.evaluated_at", "Result"),
                new("baseline", "Baseline", ReportColumnType.Text, "b.name", "Result"),
                new("score", "Device score", ReportColumnType.Number, "(SELECT sc.score FROM zentriq.sec_scores sc WHERE sc.asset_id = s.asset_id AND sc.baseline_id = s.baseline_id)", "Result"),
                .. DeviceColumns,
            ], Permission: "security.view"),

        new("changes", "Hardware & software changes", "One row per change found between two inventories of a device (kept 365 days).",
            """
            zentriq.asset_changes c
            JOIN zentriq.assets a ON a.id = c.asset_id
            LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
            LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
            WHERE a.status = 'active'
            """,
            [
                new("detected", "Detected", ReportColumnType.Date, "c.detected_at", "Change"),
                new("kind", "Hardware / software", ReportColumnType.Text, "c.kind", "Change"),
                new("change", "Change", ReportColumnType.Text, "c.change", "Change"),
                new("component", "Component", ReportColumnType.Text, "c.component", "Change"),
                new("item", "Item", ReportColumnType.Text, "c.item", "Change"),
                new("old_value", "Before", ReportColumnType.Text, "c.old_value", "Change"),
                new("new_value", "After", ReportColumnType.Text, "c.new_value", "Change"),
                new("change_source", "Detected by", ReportColumnType.Text, "c.source", "Change"),
                .. DeviceColumns,
            ]),

        new("software_titles", "Software titles", "One row per application title: publisher, devices, versions in use.",
            """
            (SELECT s.name, min(s.publisher) AS publisher, count(DISTINCT s.asset_id) AS devices, count(DISTINCT s.version) AS versions,
                    (array_agg(s.version ORDER BY zentriq.version_key(s.version) DESC NULLS LAST))[1] AS latest_version,
                    (array_agg(s.version ORDER BY zentriq.version_key(s.version) ASC NULLS LAST))[1] AS oldest_version,
                    min(s.installed_on) AS first_installed, max(s.installed_on) AS last_installed
               FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id
              WHERE a.status = 'active'
              GROUP BY s.name) st
            """,
            [
                new("software", "Software", ReportColumnType.Text, "st.name", "Software"),
                new("publisher", "Publisher", ReportColumnType.Text, "st.publisher", "Software"),
                new("devices", "Devices", ReportColumnType.Number, "st.devices", "Usage"),
                new("versions", "Versions in use", ReportColumnType.Number, "st.versions", "Usage"),
                new("latest_version", "Newest version", ReportColumnType.Text, "st.latest_version", "Usage"),
                new("oldest_version", "Oldest version", ReportColumnType.Text, "st.oldest_version", "Usage"),
                new("first_installed", "First installed", ReportColumnType.Date, "st.first_installed::timestamptz", "Usage"),
                new("last_installed", "Last installed", ReportColumnType.Date, "st.last_installed::timestamptz", "Usage"),
                new("open_vulns", "Open vulnerabilities", ReportColumnType.Number,
                    "(SELECT count(*) FROM zentriq.vuln_findings f WHERE f.software_name = st.name AND f.status IN ('open','in_progress'))", "Security"),
            ]),

        new("network_devices", "Network devices", "One row per switch, router, firewall, access point or other network device.",
            """
            zentriq.assets a
            LEFT JOIN zentriq.network_devices nd ON nd.asset_id = a.id
            LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
            LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
            WHERE a.status = 'active' AND (a.category = 'network_device' OR nd.asset_id IS NOT NULL)
            """,
            [
                new("manufacturer", "Manufacturer", ReportColumnType.Text, "COALESCE(a.manufacturer, h.vendor)", "Network device"),
                new("model", "Model", ReportColumnType.Text, "COALESCE(a.model, h.model)", "Network device"),
                new("serial", "Serial number", ReportColumnType.Text, "COALESCE(zentriq.hw_value(h.serial_number), zentriq.hw_value(a.serial_number), a.manual_serial)", "Network device"),
                new("firmware", "Firmware / software version", ReportColumnType.Text, "d.document #>> '{os,version}'", "Network device"),
                new("management", "Management", ReportColumnType.Text, Inventory.NetworkStore.ManagementExpr, "Network device"),
                new("snmp_version", "SNMP version", ReportColumnType.Text, "nd.snmp_version", "Network device"),
                new("location", "Location", ReportColumnType.Text, "d.document #>> '{device,location}'", "Network device"),
                new("ports_total", "Ports", ReportColumnType.Number, "COALESCE(nd.ports_total, (d.document #>> '{ports,physical}')::int)", "Ports"),
                new("ports_up", "Ports up", ReportColumnType.Number, "COALESCE(nd.ports_up, (d.document #>> '{ports,up}')::int)", "Ports"),
                new("ports_free", "Ports free", ReportColumnType.Number, "(SELECT count(*) FROM zentriq.network_interfaces i WHERE i.asset_id = a.id AND i.type = 'ethernet' AND i.oper <> 'up')", "Ports"),
                new("port_use_pct", "Port use (%)", ReportColumnType.Number, "round(nd.ports_up * 100.0 / NULLIF(nd.ports_total, 0))", "Ports"),
                new("ports_disabled", "Ports disabled", ReportColumnType.Number, "nd.ports_admin_down", "Ports"),
                new("ports_errors", "Ports with errors", ReportColumnType.Number, "(SELECT count(*) FROM zentriq.network_interfaces i WHERE i.asset_id = a.id AND COALESCE(i.in_errors, 0) + COALESCE(i.out_errors, 0) > 0)", "Ports"),
                new("unmanaged_ports", "Unmanaged switches behind", ReportColumnType.Number, "(SELECT count(*) FROM zentriq.network_interfaces i WHERE i.asset_id = a.id AND i.unmanaged_suspect)", "Ports"),
                new("vlans", "VLANs", ReportColumnType.Number, "nd.vlans", "Switching"),
                new("mac_entries", "MAC addresses learned", ReportColumnType.Number, "nd.mac_entries", "Switching"),
                new("neighbors", "LLDP/CDP neighbours", ReportColumnType.Number, "nd.neighbors", "Switching"),
                new("stp_root", "Spanning-tree root", ReportColumnType.YesNo, "CASE WHEN nd.stp_root THEN 'yes' WHEN nd.stp_root = false THEN 'no' END", "Switching"),
                new("stack_members", "Stack members", ReportColumnType.Number, "nd.stack_members", "Switching"),
                new("routing", "IP routing", ReportColumnType.YesNo, "CASE WHEN nd.ip_forwarding THEN 'yes' WHEN nd.ip_forwarding = false THEN 'no' END", "Routing"),
                new("routes", "Routes", ReportColumnType.Number, "nd.routes", "Routing"),
                new("arp_entries", "ARP entries", ReportColumnType.Number, "nd.arp_entries", "Routing"),
                new("poe_budget", "PoE budget (W)", ReportColumnType.Number, "nd.poe_budget_w", "Power"),
                new("poe_used", "PoE used (W)", ReportColumnType.Number, "nd.poe_used_w", "Power"),
                new("cpu", "CPU (%)", ReportColumnType.Number, "nd.cpu_percent", "Health"),
                new("memory", "Memory used (%)", ReportColumnType.Number, "nd.memory_percent", "Health"),
                new("temperature", "Temperature (°C)", ReportColumnType.Number, "nd.temperature_c", "Health"),
                new("components_bad", "Fans / power supplies not OK", ReportColumnType.Number, "nd.components_bad", "Health"),
                new("peers", "BGP / OSPF neighbours", ReportColumnType.Number, "jsonb_array_length(COALESCE(nd.routing_peers, '[]'))", "Routing"),
                new("peers_down", "Neighbours down", ReportColumnType.Number, "nd.peers_down", "Routing"),
                new("busy_ports", "Busy ports (≥ 70 %)", ReportColumnType.Number, "nd.busy_ports", "Ports"),
                new("transceivers", "Transceivers", ReportColumnType.Number, "(SELECT count(*) FROM zentriq.network_modules m WHERE m.asset_id = a.id AND m.class = 'transceiver')", "Ports"),
                new("last_boot", "Last boot", ReportColumnType.Date, "(d.document #>> '{os,lastBoot}')::timestamptz", "Health"),
                new("collected", "Collected", ReportColumnType.Date, "nd.collected_at", "Health"),
                new("last_seen", "Last seen", ReportColumnType.Date, "a.last_seen_at", "Health"),
                .. DeviceColumns,
            ]),

        new("network_ports", "Switch ports", "One row per port of a network device, with what is connected to it.",
            """
            zentriq.network_interfaces i
            JOIN zentriq.assets a ON a.id = i.asset_id
            LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
            LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
            WHERE a.status = 'active' AND i.type IN ('ethernet', 'lag', 'wireless')
            """,
            [
                new("port", "Port", ReportColumnType.Text, "i.name", "Port"),
                new("port_index", "Port index", ReportColumnType.Number, "i.if_index", "Port"),
                new("port_description", "Port description", ReportColumnType.Text, "COALESCE(NULLIF(i.alias, ''), i.description)", "Port"),
                new("port_type", "Port type", ReportColumnType.Text, "i.type", "Port"),
                new("admin_status", "Admin status", ReportColumnType.Text, "i.admin", "Port"),
                new("oper_status", "Link", ReportColumnType.Text, "i.oper", "Port"),
                new("speed", "Speed (Mbit/s)", ReportColumnType.Number, "i.speed_mbps", "Port"),
                new("duplex", "Duplex", ReportColumnType.Text, "i.duplex", "Port"),
                new("vlan", "VLAN", ReportColumnType.Number, "i.vlan", "Port"),
                new("trunk", "Trunk", ReportColumnType.YesNo, "CASE WHEN i.trunk THEN 'yes' ELSE 'no' END", "Port"),
                new("poe", "PoE delivering", ReportColumnType.YesNo, "CASE WHEN i.poe THEN 'yes' WHEN i.poe = false THEN 'no' END", "Port"),
                new("errors", "Errors", ReportColumnType.Number, "COALESCE(i.in_errors, 0) + COALESCE(i.out_errors, 0)", "Port"),
                new("discards", "Discards", ReportColumnType.Number, "COALESCE(i.in_discards, 0) + COALESCE(i.out_discards, 0)", "Port"),
                new("in_mbps", "Traffic in (Mbit/s)", ReportColumnType.Number, "round(i.in_bps / 1000000.0, 1)", "Traffic"),
                new("out_mbps", "Traffic out (Mbit/s)", ReportColumnType.Number, "round(i.out_bps / 1000000.0, 1)", "Traffic"),
                new("util", "Utilisation (%)", ReportColumnType.Number, "i.util_percent", "Traffic"),
                new("stp_state", "Spanning tree", ReportColumnType.Text, "i.stp_state", "Port"),
                new("mac_count", "Devices learned", ReportColumnType.Number, "i.mac_count", "Connected"),
                new("connected", "Connected devices", ReportColumnType.Text, "(SELECT string_agg(COALESCE(x ->> 'name', '') || COALESCE(' (' || (x ->> 'ip') || ')', ''), ', ') FROM jsonb_array_elements(i.connected) x)", "Connected"),
                new("neighbor", "Neighbour (LLDP/CDP)", ReportColumnType.Text, "i.neighbor", "Connected"),
                new("uplink", "Uplink", ReportColumnType.YesNo, "CASE WHEN i.uplink THEN 'yes' ELSE 'no' END", "Connected"),
                new("unmanaged", "Unmanaged switch suspected", ReportColumnType.YesNo, "CASE WHEN i.unmanaged_suspect THEN 'yes' ELSE 'no' END", "Connected"),
                .. DeviceColumns,
            ]),

        new("network_neighbors", "Network links (LLDP/CDP)", "One row per neighbour reported by a network device: the network topology.",
            """
            zentriq.network_neighbors nb
            JOIN zentriq.assets a ON a.id = nb.asset_id
            LEFT JOIN zentriq.assets ra ON ra.id = nb.remote_asset_id
            LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
            LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
            WHERE a.status = 'active'
            """,
            [
                new("protocol", "Protocol", ReportColumnType.Text, "upper(nb.protocol)", "Link"),
                new("local_port", "Local port", ReportColumnType.Text, "nb.local_port", "Link"),
                new("remote_name", "Neighbour", ReportColumnType.Text, "COALESCE(nb.remote_name, nb.remote_chassis_id)", "Link"),
                new("remote_port", "Neighbour port", ReportColumnType.Text, "nb.remote_port", "Link"),
                new("remote_platform", "Neighbour platform", ReportColumnType.Text, "COALESCE(nb.remote_platform, left(nb.remote_description, 120))", "Link"),
                new("remote_ip", "Neighbour IP address", ReportColumnType.Text, "host(nb.remote_address)", "Link"),
                new("capabilities", "Neighbour capabilities", ReportColumnType.Text, "array_to_string(nb.capabilities, ', ')", "Link"),
                new("remote_inventoried", "Neighbour in inventory", ReportColumnType.YesNo, "CASE WHEN nb.remote_asset_id IS NOT NULL THEN 'yes' ELSE 'no' END", "Link"),
                .. DeviceColumns,
            ]),

        new("nonit_assets", "Non-IT assets", "One row per non-IT asset: furniture, appliances, vehicles, tools and other items.",
            """
            zentriq.nonit_assets n
            LEFT JOIN zentriq.nonit_categories c ON c.id = n.category_id
            LEFT JOIN zentriq.departments dp ON dp.id = n.department_id
            """,
            [
                new("asset_tag", "Asset tag", ReportColumnType.Text, "n.asset_tag", "Asset"),
                new("name", "Name", ReportColumnType.Text, "n.name", "Asset"),
                new("category", "Category", ReportColumnType.Text, "c.name", "Asset"),
                new("category_group", "Category group", ReportColumnType.Text, "c.group_name", "Asset"),
                new("manufacturer", "Manufacturer", ReportColumnType.Text, "n.manufacturer", "Asset"),
                new("model", "Model", ReportColumnType.Text, "n.model", "Asset"),
                new("serial", "Serial number", ReportColumnType.Text, "n.serial_number", "Asset"),
                new("barcode", "Barcode", ReportColumnType.Text, "COALESCE(n.barcode, n.asset_tag)", "Asset"),
                new("status", "Status", ReportColumnType.Text, "replace(initcap(replace(n.status, '_', ' ')), 'Lost', 'Lost / missing')", "Asset"),
                new("condition", "Condition", ReportColumnType.Text, "initcap(n.condition)", "Asset"),
                new("quantity", "Quantity", ReportColumnType.Number, "n.quantity", "Asset"),
                new("location", "Location", ReportColumnType.Text, "n.location", "Where"),
                new("department", "Department / unit", ReportColumnType.Text, "dp.name", "Where"),
                new("assigned_to", "Assigned to", ReportColumnType.Text, "n.assigned_to", "Where"),
                new("purchase_date", "Purchase date", ReportColumnType.Date, "n.purchase_date::timestamptz", "Purchase"),
                new("vendor", "Vendor", ReportColumnType.Text, "n.vendor", "Purchase"),
                new("cost", "Purchase cost", ReportColumnType.Number, "n.purchase_cost", "Purchase"),
                new("total_cost", "Purchase cost × quantity", ReportColumnType.Number, "n.purchase_cost * GREATEST(n.quantity, 1)", "Purchase"),
                new("current_value", "Current value (straight-line)", ReportColumnType.Number, NonIt.NonItFields.CurrentValueSql, "Purchase"),
                new("invoice", "Invoice number", ReportColumnType.Text, "n.invoice_number", "Purchase"),
                new("po", "PO number", ReportColumnType.Text, "n.po_number", "Purchase"),
                new("age_years", "Age (years)", ReportColumnType.Number, "round((current_date - n.purchase_date) / 365.25, 1)", "Purchase"),
                new("warranty_until", "Warranty until", ReportColumnType.Date, "n.warranty_until::timestamptz", "Cover"),
                new("amc_vendor", "AMC vendor", ReportColumnType.Text, "n.amc_vendor", "Cover"),
                new("amc_until", "AMC until", ReportColumnType.Date, "n.amc_until::timestamptz", "Cover"),
                new("insurance_until", "Insurance until", ReportColumnType.Date, "n.insurance_until::timestamptz", "Cover"),
                new("warranty_days", "Days of warranty left", ReportColumnType.Number, "(n.warranty_until - current_date)", "Cover"),
                new("last_maintenance", "Last maintenance", ReportColumnType.Date, "n.last_maintenance::timestamptz", "Maintenance"),
                new("next_maintenance", "Next maintenance", ReportColumnType.Date, "n.next_maintenance::timestamptz", "Maintenance"),
                new("maintenance_days", "Days to next maintenance", ReportColumnType.Number, "(n.next_maintenance - current_date)", "Maintenance"),
                new("disposed_on", "Disposed on", ReportColumnType.Date, "n.disposed_on::timestamptz", "Asset"),
                new("notes", "Notes", ReportColumnType.Text, "left(n.notes, 500)", "Asset"),
                new("added", "Added", ReportColumnType.Date, "n.created_at", "Asset"),
                new("updated", "Last changed", ReportColumnType.Date, "n.updated_at", "Asset"),
            ]),

        new("accessory_stock", "IT accessory stock", "One row per IT accessory and location: quantity on hand, faulty units, reorder level and value.",
            """
            zentriq.accessory_stock s
            JOIN zentriq.accessory_items i ON i.id = s.item_id
            JOIN zentriq.locations lo ON lo.id = s.location_id
            """,
            [
                new("location", "Location", ReportColumnType.Text, "lo.name", "Where"),
                new("item", "Accessory", ReportColumnType.Text, "i.name", "Accessory"),
                new("category", "Category", ReportColumnType.Text, "i.category", "Accessory"),
                new("brand", "Brand", ReportColumnType.Text, "i.brand", "Accessory"),
                new("model", "Model", ReportColumnType.Text, "i.model", "Accessory"),
                new("sku", "SKU / part number", ReportColumnType.Text, "i.sku", "Accessory"),
                new("returnable", "Returnable", ReportColumnType.YesNo, "CASE WHEN i.returnable THEN 'yes' ELSE 'no' END", "Accessory"),
                new("quantity", "In stock", ReportColumnType.Number, "s.quantity", "Stock"),
                new("faulty", "Faulty", ReportColumnType.Number, "s.faulty", "Stock"),
                new("reorder_level", "Reorder level", ReportColumnType.Number, "i.reorder_level", "Stock"),
                new("below_reorder", "At or below reorder level", ReportColumnType.YesNo, "CASE WHEN i.reorder_level > 0 AND s.quantity <= i.reorder_level THEN 'yes' ELSE 'no' END", "Stock"),
                new("unit_cost", "Unit cost", ReportColumnType.Number, "i.unit_cost", "Stock"),
                new("stock_value", "Stock value", ReportColumnType.Number, "s.quantity * COALESCE(i.unit_cost, 0)", "Stock"),
                new("updated", "Last change", ReportColumnType.Date, "s.updated_at", "Stock"),
            ]),

        new("accessory_movements", "IT accessory movements", "One row per stock movement: received, issued, returned, transferred, written off or counted.",
            """
            zentriq.accessory_movements m
            JOIN zentriq.accessory_items i ON i.id = m.item_id
            JOIN zentriq.locations lo ON lo.id = m.location_id
            LEFT JOIN zentriq.locations lt ON lt.id = m.to_location_id
            LEFT JOIN zentriq.departments dp ON dp.id = m.department_id
            LEFT JOIN zentriq.assets a ON a.id = m.asset_id
            """,
            [
                new("occurred", "Date", ReportColumnType.Date, "m.occurred_at", "Movement"),
                new("kind", "Movement", ReportColumnType.Text, "m.kind", "Movement"),
                new("item", "Accessory", ReportColumnType.Text, "i.name", "Accessory"),
                new("category", "Category", ReportColumnType.Text, "i.category", "Accessory"),
                new("quantity", "Quantity change", ReportColumnType.Number, "m.quantity + m.faulty_change", "Movement"),
                new("quantity_abs", "Quantity", ReportColumnType.Number, "abs(m.quantity + m.faulty_change)", "Movement"),
                new("location", "Location", ReportColumnType.Text, "lo.name", "Where"),
                new("to_location", "To location", ReportColumnType.Text, "lt.name", "Where"),
                new("person", "Person", ReportColumnType.Text, "m.person", "Who"),
                new("department", "Department / unit", ReportColumnType.Text, "dp.name", "Who"),
                new("device", "Device", ReportColumnType.Text, "COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip))", "Who"),
                new("condition", "Condition", ReportColumnType.Text, "m.condition", "Movement"),
                new("reason", "Reason", ReportColumnType.Text, "m.reason", "Movement"),
                new("reference", "Reference (invoice / PO / ticket)", ReportColumnType.Text, "m.reference", "Movement"),
                new("vendor", "Vendor", ReportColumnType.Text, "m.vendor", "Movement"),
                new("unit_cost", "Unit cost", ReportColumnType.Number, "m.unit_cost", "Movement"),
                new("value", "Value", ReportColumnType.Number, "abs(m.quantity + m.faulty_change) * COALESCE(m.unit_cost, i.unit_cost, 0)", "Movement"),
                new("serials", "Serial numbers", ReportColumnType.Text, "array_to_string(m.serials, ', ')", "Movement"),
                new("by", "Recorded by", ReportColumnType.Text, "m.by_name", "Movement"),
                new("note", "Note", ReportColumnType.Text, "left(m.note, 500)", "Movement"),
            ]),

        new("asset_lifecycle", "IT asset lifecycle", "One row per IT asset: lifecycle stage and status, asset tag, custodian, purchase, depreciation and book value, warranty, lease and disposal.",
            """
            zentriq.v_asset_lifecycle v
            LEFT JOIN zentriq.departments dp ON dp.id = v.department_id
            LEFT JOIN zentriq.locations lo ON lo.id = v.location_id
            """,
            [
                new("asset_tag", "Asset tag", ReportColumnType.Text, "v.asset_tag", "Asset"),
                new("device", "Device", ReportColumnType.Text, "COALESCE(v.hostname, v.serial, v.model)", "Asset"),
                new("serial", "Serial number", ReportColumnType.Text, "v.serial", "Asset"),
                new("manufacturer", "Manufacturer", ReportColumnType.Text, "v.manufacturer", "Asset"),
                new("model", "Model", ReportColumnType.Text, "v.model", "Asset"),
                new("class", "Device class", ReportColumnType.Text, "v.class_label", "Asset"),
                new("os", "Operating system", ReportColumnType.Text, "v.os_name", "Asset"),
                new("stage", "Lifecycle stage", ReportColumnType.Text, StageExpr, "Lifecycle"),
                new("status", "Lifecycle status", ReportColumnType.Text, "initcap(replace(v.status, '_', ' '))", "Lifecycle"),
                new("stage_changed", "Stage since", ReportColumnType.Date, "v.stage_changed_at", "Lifecycle"),
                new("stage_reason", "Reason", ReportColumnType.Text, "v.stage_reason", "Lifecycle"),
                new("age_months", "Age (months)", ReportColumnType.Number, "v.age_months", "Lifecycle"),
                new("refresh_due", "Due for refresh", ReportColumnType.YesNo, "CASE WHEN v.refresh_due THEN 'yes' ELSE 'no' END", "Lifecycle"),
                new("assigned_to", "Assigned to", ReportColumnType.Text, "v.assigned_to", "Custody"),
                new("assigned_email", "Custodian e-mail", ReportColumnType.Text, "v.assigned_email", "Custody"),
                new("assigned_at", "Assigned on", ReportColumnType.Date, "v.assigned_at", "Custody"),
                new("expected_return", "Loan ends", ReportColumnType.Date, "v.expected_return", "Custody"),
                new("department", "Department / unit", ReportColumnType.Text, "dp.name", "Custody"),
                new("location", "Location", ReportColumnType.Text, "lo.name", "Custody"),
                new("acquisition", "Acquisition", ReportColumnType.Text, "initcap(v.acquisition)", "Purchase"),
                new("purchase_date", "Purchase date", ReportColumnType.Date, "v.purchase_date", "Purchase"),
                new("purchase_cost", "Purchase cost", ReportColumnType.Number, "v.purchase_cost", "Purchase"),
                new("currency", "Currency", ReportColumnType.Text, "v.currency", "Purchase"),
                new("vendor", "Vendor", ReportColumnType.Text, "v.vendor", "Purchase"),
                new("invoice_number", "Invoice", ReportColumnType.Text, "v.invoice_number", "Purchase"),
                new("po_number", "Purchase order", ReportColumnType.Text, "v.po_number", "Purchase"),
                new("depreciation_method", "Depreciation", ReportColumnType.Text, "initcap(replace(v.depreciation_method, '_', ' '))", "Purchase"),
                new("useful_life_months", "Useful life (months)", ReportColumnType.Number, "v.useful_life_months", "Purchase"),
                new("book_value", "Book value", ReportColumnType.Number, "v.book_value", "Purchase"),
                new("depreciated", "Depreciated so far", ReportColumnType.Number, "v.purchase_cost - v.book_value", "Purchase"),
                new("lessor", "Lessor", ReportColumnType.Text, "v.lessor", "Lease"),
                new("lease_end", "Lease ends", ReportColumnType.Date, "v.lease_end", "Lease"),
                new("lease_monthly_cost", "Lease cost / month", ReportColumnType.Number, "v.lease_monthly_cost", "Lease"),
                new("warranty_provider", "Warranty provider", ReportColumnType.Text, "v.warranty_provider", "Warranty"),
                new("warranty_type", "Warranty type", ReportColumnType.Text, "v.warranty_type", "Warranty"),
                new("warranty_end", "Warranty ends", ReportColumnType.Date, "v.warranty_end", "Warranty"),
                new("warranty_days", "Warranty days left", ReportColumnType.Number, "v.warranty_days", "Warranty"),
                new("contracts", "Contracts", ReportColumnType.Text, "(SELECT string_agg(c.name, ', ' ORDER BY c.end_date) FROM zentriq.asset_contract_assets ca JOIN zentriq.asset_contracts c ON c.id = ca.contract_id WHERE ca.asset_id = v.asset_id)", "Warranty"),
                new("disposed_on", "Disposed on", ReportColumnType.Date, "v.disposed_on", "Disposal"),
                new("disposal_method", "Disposal method", ReportColumnType.Text, "initcap(replace(v.disposal_method, '_', ' '))", "Disposal"),
                new("disposal_vendor", "Disposal vendor", ReportColumnType.Text, "v.disposal_vendor", "Disposal"),
                new("disposal_value", "Disposal value", ReportColumnType.Number, "v.disposal_value", "Disposal"),
                new("sanitization_method", "Data sanitisation (NIST SP 800-88)", ReportColumnType.Text, "initcap(replace(v.sanitization_method, '_', ' '))", "Disposal"),
                new("sanitized_by", "Sanitised by", ReportColumnType.Text, "v.sanitized_by", "Disposal"),
                new("sanitized_on", "Sanitised on", ReportColumnType.Date, "v.sanitized_on", "Disposal"),
                new("certificate_ref", "Certificate", ReportColumnType.Text, "v.certificate_ref", "Disposal"),
                new("last_seen", "Last seen", ReportColumnType.Date, "v.last_seen_at", "Asset"),
            ]),

        new("asset_lifecycle_events", "IT asset lifecycle history", "One row per lifecycle change: stage, assignment, return, disposal and automatic changes.",
            """
            zentriq.asset_lifecycle_events e
            JOIN zentriq.assets a ON a.id = e.asset_id
            JOIN zentriq.asset_lifecycle l ON l.asset_id = a.id
            """,
            [
                new("at", "Date", ReportColumnType.Date, "e.at", "Change"),
                new("device", "Device", ReportColumnType.Text, "COALESCE(a.hostname, a.serial_number, a.manual_serial)", "Asset"),
                new("asset_tag", "Asset tag", ReportColumnType.Text, "l.asset_tag", "Asset"),
                new("kind", "Change", ReportColumnType.Text, "initcap(replace(e.kind, '_', ' '))", "Change"),
                new("from_stage", "From", ReportColumnType.Text, "initcap(replace(e.from_stage, '_', ' '))", "Change"),
                new("to_stage", "To", ReportColumnType.Text, "initcap(replace(e.to_stage, '_', ' '))", "Change"),
                new("to_status", "Status", ReportColumnType.Text, "initcap(replace(e.to_status, '_', ' '))", "Change"),
                new("person", "Person", ReportColumnType.Text, "e.person", "Change"),
                new("reason", "Reason", ReportColumnType.Text, "left(e.reason, 500)", "Change"),
                new("by", "By", ReportColumnType.Text, "e.actor", "Change"),
            ]),

        new("cloud_resources", "Cloud and virtual resources", "One row per virtual machine, host, datastore and cloud resource from the Azure, AWS, vCenter and Hyper-V connections.",
            """
            zentriq.cloud_resources cr
            JOIN zentriq.cloud_accounts ca ON ca.id = cr.account_id
            LEFT JOIN zentriq.assets a ON a.id = cr.asset_id
            LEFT JOIN zentriq.departments dp ON dp.id = cr.department_id
            LEFT JOIN zentriq.locations lo ON lo.id = cr.location_id
            """,
            [
                new("provider", "Platform", ReportColumnType.Text, CloudProviderLabel("cr.provider"), "Resource"),
                new("account", "Connection", ReportColumnType.Text, "ca.name", "Resource"),
                new("kind", "Type", ReportColumnType.Text, CloudKindLabel("cr.kind"), "Resource"),
                new("name", "Name", ReportColumnType.Text, "cr.name", "Resource"),
                new("external_id", "Resource ID", ReportColumnType.Text, "cr.external_id", "Resource"),
                new("region", "Region / site", ReportColumnType.Text, "cr.region", "Resource"),
                new("group", "Resource group / VPC / cluster", ReportColumnType.Text, "cr.resource_group", "Resource"),
                new("state", "State", ReportColumnType.Text, "cr.state", "Resource"),
                new("state_since", "In this state since", ReportColumnType.Date, "cr.state_since", "Resource"),
                new("size", "Size / SKU", ReportColumnType.Text, "cr.size", "Resource"),
                new("os", "Operating system / engine", ReportColumnType.Text, "cr.os", "Resource"),
                new("hostname", "Host name", ReportColumnType.Text, "cr.hostname", "Network"),
                new("private_ip", "Private IP", ReportColumnType.Text, "cr.private_ip", "Network"),
                new("public_ip", "Public IP", ReportColumnType.Text, "cr.public_ip", "Network"),
                new("cpu", "vCPU", ReportColumnType.Number, "cr.cpu", "Capacity"),
                new("memory_gb", "Memory (GB)", ReportColumnType.Number, "round(cr.memory_mb / 1024.0, 1)", "Capacity"),
                new("disk_gb", "Storage (GB)", ReportColumnType.Number, "cr.disk_gb", "Capacity"),
                new("tags", "Tags", ReportColumnType.Text, "(SELECT string_agg(t.key || '=' || t.value, ', ' ORDER BY t.key) FROM jsonb_each_text(cr.tags) t)", "Ownership"),
                new("department", "Department", ReportColumnType.Text, "dp.name", "Ownership"),
                new("location", "Location", ReportColumnType.Text, "lo.name", "Ownership"),
                new("device", "Inventoried device", ReportColumnType.Text, "COALESCE(a.hostname, a.serial_number)", "Ownership"),
                new("linked", "Linked to inventory", ReportColumnType.YesNo, "CASE WHEN cr.asset_id IS NULL THEN 'no' ELSE 'yes' END", "Ownership"),
                new("open_findings", "Open findings", ReportColumnType.Number,
                    "(SELECT count(*) FROM zentriq.cloud_findings f WHERE f.resource_id = cr.id AND f.resolved_at IS NULL AND NOT f.ignored)", "Findings"),
                new("created", "Created at provider", ReportColumnType.Date, "cr.created_at_provider", "Dates"),
                new("first_seen", "First seen", ReportColumnType.Date, "cr.first_seen_at", "Dates"),
                new("last_seen", "Last seen", ReportColumnType.Date, "cr.last_seen_at", "Dates"),
                new("deleted", "Deleted", ReportColumnType.YesNo, "CASE WHEN cr.deleted_at IS NULL THEN 'no' ELSE 'yes' END", "Dates"),
            ]),

        new("cloud_findings", "Cloud and virtualization findings", "One row per finding: exposure to the internet, public or unencrypted storage, idle and unattached resources, old checkpoints, capacity.",
            """
            zentriq.cloud_findings f
            JOIN zentriq.cloud_resources cr ON cr.id = f.resource_id
            JOIN zentriq.cloud_accounts ca ON ca.id = cr.account_id
            """,
            [
                new("severity", "Severity", ReportColumnType.Text, "initcap(f.severity)", "Finding"),
                new("severity_rank", "Severity rank", ReportColumnType.Number, "array_position(ARRAY['info','low','medium','high','critical'], f.severity)", "Finding"),
                new("title", "Finding", ReportColumnType.Text, "f.title", "Finding"),
                new("detail", "Detail", ReportColumnType.Text, "left(f.detail, 500)", "Finding"),
                new("rule", "Rule", ReportColumnType.Text, "f.rule", "Finding"),
                new("status", "Status", ReportColumnType.Text, "CASE WHEN f.resolved_at IS NOT NULL OR cr.deleted_at IS NOT NULL THEN 'Resolved' WHEN f.ignored THEN 'Ignored' ELSE 'Open' END", "Finding"),
                new("first_seen", "First seen", ReportColumnType.Date, "f.first_seen_at", "Finding"),
                new("resolved", "Resolved", ReportColumnType.Date, "f.resolved_at", "Finding"),
                new("resource", "Resource", ReportColumnType.Text, "COALESCE(cr.name, cr.external_id)", "Resource"),
                new("kind", "Type", ReportColumnType.Text, CloudKindLabel("cr.kind"), "Resource"),
                new("provider", "Platform", ReportColumnType.Text, CloudProviderLabel("cr.provider"), "Resource"),
                new("account", "Connection", ReportColumnType.Text, "ca.name", "Resource"),
                new("region", "Region / site", ReportColumnType.Text, "cr.region", "Resource"),
            ]),

        new("discovery", "Discovery results", "One row per address checked by a discovery scan.",
            """
            zentriq.scan_results r
            JOIN zentriq.discovery_scans sc ON sc.id = r.scan_id
            """,
            [
                new("scan", "Scan", ReportColumnType.Text, "sc.name", "Scan"),
                new("scan_number", "Scan number", ReportColumnType.Number, "sc.number", "Scan"),
                new("scan_type", "Scan type", ReportColumnType.Text, "sc.scan_type", "Scan"),
                new("started", "Scan started", ReportColumnType.Date, "COALESCE(sc.started_at, sc.created_at)", "Scan"),
                new("target", "Target", ReportColumnType.Text, "r.target", "Result"),
                new("ip", "IP address", ReportColumnType.Text, "host(r.ip_address)", "Result"),
                new("hostname", "Hostname", ReportColumnType.Text, "r.hostname", "Result"),
                new("result", "Result", ReportColumnType.Text, "r.status", "Result"),
                new("protocol", "Protocol", ReportColumnType.Text, "r.protocol", "Result"),
                new("open_ports", "Open ports", ReportColumnType.Text, "array_to_string(r.open_ports, ', ')", "Result"),
                new("detail", "Detail", ReportColumnType.Text, "r.detail", "Result"),
                new("asset_created", "New device", ReportColumnType.YesNo, "CASE WHEN r.asset_created THEN 'yes' ELSE 'no' END", "Result"),
                new("duration_ms", "Duration (ms)", ReportColumnType.Number, "r.duration_ms", "Result"),
                new("recorded", "Checked at", ReportColumnType.Date, "r.recorded_at", "Result"),
            ]),

        new("audit", "Audit trail", "Administrative actions recorded in the tamper-evident audit log.",
            "zentriq.audit_events e",
            [
                new("at", "Time", ReportColumnType.Date, "e.occurred_at", "Event"),
                new("user", "User", ReportColumnType.Text, "e.actor_name", "Event"),
                new("action", "Action", ReportColumnType.Text, "e.action", "Event"),
                new("target", "Target", ReportColumnType.Text, "e.target", "Event"),
                new("ip", "IP address", ReportColumnType.Text, "e.source_ip", "Event"),
                new("result", "Result", ReportColumnType.Text, "e.outcome", "Event"),
                new("detail", "Detail", ReportColumnType.Text, "left(e.detail::text, 500)", "Event"),
            ], Permission: "audit.view"),
    ];

    public static ReportDataset? Find(string key) => Datasets.FirstOrDefault(d => d.Key == key);
}
