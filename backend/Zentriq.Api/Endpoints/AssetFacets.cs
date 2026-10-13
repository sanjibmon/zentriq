namespace Zentriq.Api.Endpoints;

/// <summary>
/// One definition per dashboard dimension, used both to draw the chart and to filter the asset list when the chart is
/// clicked, so a slice and the list it opens always agree. Expressions use the aliases of <see cref="From"/>.
/// </summary>
public static class AssetFacets
{
    public const string From = """
        FROM zentriq.assets a
        LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
        LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
        LEFT JOIN LATERAL (SELECT r.id, r.last_checkin_at FROM zentriq.agent_registrations r
                            WHERE r.asset_id = a.id AND r.status = 'active' ORDER BY r.last_checkin_at DESC NULLS LAST LIMIT 1) ag ON true
        """;

    private const string LastContact = "COALESCE(GREATEST(ag.last_checkin_at, a.last_seen_at), a.last_seen_at)";
    private const string DiskGb = "(SELECT sum((x ->> 'sizeBytes')::numeric) / 1e9 FROM jsonb_array_elements(CASE WHEN jsonb_typeof(d.document #> '{hardware,disks}') = 'array' THEN d.document #> '{hardware,disks}' ELSE '[]' END) x)";
    private const string MinFree = "(SELECT min((v ->> 'freeBytes')::numeric / NULLIF((v ->> 'sizeBytes')::numeric, 0)) FROM jsonb_array_elements(CASE WHEN jsonb_typeof(d.document #> '{hardware,volumes}') = 'array' THEN d.document #> '{hardware,volumes}' ELSE '[]' END) v WHERE (v ->> 'sizeBytes')::numeric > 0 AND v ->> 'freeBytes' IS NOT NULL)";
    private static string YesNo(string path) => $"CASE WHEN d.asset_id IS NULL OR a.os_family IS DISTINCT FROM 'windows' THEN NULL WHEN d.document #>> '{path}' = 'true' THEN 'yes' WHEN d.document #>> '{path}' = 'false' THEN 'no' ELSE 'unknown' END";

    public static readonly IReadOnlyDictionary<string, string> Expr = new Dictionary<string, string>
    {
        ["category"] = "a.category",
        ["os"] = "COALESCE(a.os_name, initcap(a.os_family), 'Unknown')",
        ["criticality"] = "a.criticality",
        ["inventory"] = "a.inventory_status",
        ["type"] = "COALESCE(d.document #>> '{device,chassis}', h.chassis, CASE WHEN a.category IN ('network_device','printer','iot') THEN a.category ELSE 'not inventoried' END)",
        ["coverage"] = "CASE WHEN ag.id IS NOT NULL THEN 'agent' WHEN a.inventory_status = 'success' THEN 'agentless' ELSE 'discovered only' END",
        ["health"] = "CASE WHEN ag.id IS NULL THEN NULL WHEN ag.last_checkin_at IS NULL THEN 'never' WHEN ag.last_checkin_at > now() - interval '45 minutes' THEN 'online' WHEN ag.last_checkin_at > now() - interval '7 days' THEN 'stale' ELSE 'offline' END",
        ["seen"] = $"CASE WHEN ag.id IS NOT NULL AND ag.last_checkin_at > now() - interval '30 minutes' THEN 'Online now' WHEN {LastContact} > now() - interval '1 day' THEN 'Last 24 hours' WHEN {LastContact} > now() - interval '7 days' THEN '1–7 days' WHEN {LastContact} > now() - interval '30 days' THEN '7–30 days' ELSE 'Over 30 days' END",
        ["ram"] = "CASE WHEN h.ram_gb IS NULL THEN NULL WHEN h.ram_gb < 4 THEN '< 4 GB' WHEN h.ram_gb < 8 THEN '4–8 GB' WHEN h.ram_gb < 16 THEN '8–16 GB' WHEN h.ram_gb < 32 THEN '16–32 GB' ELSE '32 GB +' END",
        ["cores"] = "CASE WHEN h.cpu_cores IS NULL THEN NULL WHEN h.cpu_cores <= 2 THEN '1–2 cores' WHEN h.cpu_cores <= 4 THEN '3–4 cores' WHEN h.cpu_cores <= 8 THEN '5–8 cores' WHEN h.cpu_cores <= 16 THEN '9–16 cores' ELSE '17+ cores' END",
        ["cpu"] = "NULLIF(btrim(regexp_replace(regexp_replace(h.cpu, '\\((R|TM|tm|r)\\)', '', 'g'), '\\s+', ' ', 'g')), '')",
        ["vendor"] = "CASE WHEN h.asset_id IS NULL THEN NULL ELSE COALESCE(NULLIF(btrim(h.vendor), ''), 'Unknown') END",
        ["disk"] = $"CASE WHEN {DiskGb} IS NULL OR {DiskGb} = 0 THEN NULL WHEN {DiskGb} < 140 THEN '< 128 GB' WHEN {DiskGb} < 280 THEN '128–256 GB' WHEN {DiskGb} < 560 THEN '256–512 GB' WHEN {DiskGb} < 1100 THEN '512 GB–1 TB' WHEN {DiskGb} < 2200 THEN '1–2 TB' ELSE '> 2 TB' END",
        ["free"] = $"CASE WHEN {MinFree} IS NULL THEN NULL WHEN {MinFree} < 0.10 THEN 'Critical (< 10% free)' WHEN {MinFree} < 0.25 THEN 'Low (10–25% free)' WHEN {MinFree} < 0.50 THEN 'OK (25–50% free)' ELSE 'Plenty (> 50% free)' END",
        ["encryption"] = YesNo("{security,diskEncryption}"),
        ["firewall"] = YesNo("{security,firewallEnabled}"),
        ["secureboot"] = YesNo("{security,secureBoot}"),
        ["tpm"] = YesNo("{hardware,tpmPresent}"),
        ["antivirus"] = "CASE WHEN d.asset_id IS NULL OR a.os_family IS DISTINCT FROM 'windows' THEN NULL WHEN jsonb_array_length(CASE WHEN jsonb_typeof(d.document #> '{security,antivirus}') = 'array' THEN d.document #> '{security,antivirus}' ELSE '[]' END) = 0 THEN 'unknown' WHEN EXISTS (SELECT 1 FROM jsonb_array_elements(d.document #> '{security,antivirus}') x WHERE x ->> 'enabled' = 'true') THEN 'yes' ELSE 'no' END",
    };

    /// <summary>Days since the asset was last heard from (agent check-in, scan or inventory).</summary>
    public const string OfflineDays = $"floor(extract(epoch FROM now() - {LastContact}) / 86400)::int";
}
