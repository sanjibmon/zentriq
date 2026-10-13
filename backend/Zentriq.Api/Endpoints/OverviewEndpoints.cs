using Npgsql;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;

namespace Zentriq.Api.Endpoints;

/// <summary>Everything the home dashboard draws, computed in one round of read-only queries.</summary>
public static class OverviewEndpoints
{
    public sealed record KeyCount(string Key, long Count);
    public sealed record Posture(string Key, long Yes, long No, long Unknown);
    public sealed record DayPoint(DateOnly Day, long New, long Total);

    public static void MapOverviewEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/dashboard/overview", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);

            async Task<List<KeyCount>> Counts(string sql)
            {
                var list = new List<KeyCount>();
                await using var cmd = new NpgsqlCommand(sql, conn);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) list.Add(new KeyCount(r.IsDBNull(0) ? "unknown" : r.GetString(0), r.GetInt64(1)));
                return list;
            }
            async Task<long> Scalar(string sql)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            }

            const string Active = "FROM zentriq.assets a WHERE a.status = 'active'";
            const string AgentOf = "(SELECT r.last_checkin_at FROM zentriq.agent_registrations r WHERE r.asset_id = a.id AND r.status = 'active' ORDER BY r.last_checkin_at DESC NULLS LAST LIMIT 1)";
            const string HasAgent = "EXISTS (SELECT 1 FROM zentriq.agent_registrations r WHERE r.asset_id = a.id AND r.status = 'active')";

            var kpi = new Dictionary<string, long>
            {
                ["assets"] = await Scalar($"SELECT count(*) {Active}"),
                ["servers"] = await Scalar($"SELECT count(*) {Active} AND a.category = 'server'"),
                ["endpoints"] = await Scalar($"SELECT count(*) {Active} AND a.category = 'endpoint'"),
                ["networkDevices"] = await Scalar($"SELECT count(*) {Active} AND a.category IN ('network_device','printer','iot')"),
                ["inventoried"] = await Scalar($"SELECT count(*) {Active} AND a.inventory_status = 'success'"),
                ["withAgent"] = await Scalar($"SELECT count(*) {Active} AND {HasAgent}"),
                ["agentsOnline"] = await Scalar("SELECT count(*) FROM zentriq.agent_registrations WHERE status = 'active' AND last_checkin_at > now() - interval '45 minutes'"),
                ["agents"] = await Scalar("SELECT count(*) FROM zentriq.agent_registrations WHERE status = 'active'"),
                ["virtual"] = await Scalar($"SELECT count(*) {Active} AND EXISTS (SELECT 1 FROM zentriq.asset_inventory_documents d WHERE d.asset_id = a.id AND d.document #>> '{{hardware,isVirtual}}' = 'true')"),
                ["stale"] = await Scalar($"SELECT count(*) {Active} AND a.last_seen_at < now() - interval '30 days'"),
                ["newLast7"] = await Scalar($"SELECT count(*) {Active} AND a.first_seen_at > now() - interval '7 days'"),
                ["retired"] = await Scalar("SELECT count(*) FROM zentriq.assets WHERE status = 'retired'"),
                ["softwareTitles"] = await Scalar($"SELECT count(DISTINCT lower(s.name)) FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id WHERE a.status = 'active'"),
                ["softwareInstalls"] = await Scalar($"SELECT count(*) FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id WHERE a.status = 'active'"),
                ["scans30"] = await Scalar("SELECT count(*) FROM zentriq.discovery_scans WHERE created_at > now() - interval '30 days'"),
                ["lowDisk"] = await Scalar($$"""
                    SELECT count(*) {{Active}} AND EXISTS (SELECT 1 FROM zentriq.asset_inventory_documents d, jsonb_array_elements(COALESCE(d.document #> '{hardware,volumes}', '[]')) v
                        WHERE d.asset_id = a.id AND (v ->> 'sizeBytes')::numeric > 0 AND (v ->> 'freeBytes')::numeric / (v ->> 'sizeBytes')::numeric < 0.10)
                    """),
            };

            var byCategory = await Counts($"SELECT a.category, count(*) {Active} GROUP BY 1 ORDER BY 2 DESC");
            var byOs = await Counts($"""
                SELECT CASE WHEN rn <= 7 THEN os ELSE 'Other' END, sum(n)::bigint FROM (
                    SELECT COALESCE(a.os_name, initcap(a.os_family), 'Unknown') AS os, count(*) AS n, row_number() OVER (ORDER BY count(*) DESC) AS rn {Active} GROUP BY 1) t
                 GROUP BY 1 ORDER BY 2 DESC
                """);
            var byInventory = await Counts($"SELECT a.inventory_status, count(*) {Active} GROUP BY 1 ORDER BY 2 DESC");
            var byCriticality = await Counts($"SELECT a.criticality, count(*) {Active} GROUP BY 1 ORDER BY 2 DESC");
            var byType = await Counts($$"""
                SELECT COALESCE(d.document #>> '{device,chassis}', h.chassis, CASE WHEN a.category IN ('network_device','printer','iot') THEN a.category ELSE 'not inventoried' END), count(*)
                  FROM zentriq.assets a LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
                 WHERE a.status = 'active' GROUP BY 1 ORDER BY 2 DESC
                """);
            var byManufacturer = await Counts($"""
                SELECT CASE WHEN rn <= 7 THEN v ELSE 'Other' END, sum(n)::bigint FROM (
                    SELECT COALESCE(NULLIF(h.vendor, ''), 'Unknown') AS v, count(*) AS n, row_number() OVER (ORDER BY count(*) DESC) AS rn
                      FROM zentriq.hardware_inventory h JOIN zentriq.assets a ON a.id = h.asset_id WHERE a.status = 'active' GROUP BY 1) t
                 GROUP BY 1 ORDER BY 2 DESC
                """);
            var coverage = await Counts($"""
                SELECT CASE WHEN {HasAgent} THEN 'agent' WHEN a.inventory_status = 'success' THEN 'agentless' ELSE 'discovered only' END, count(*) {Active} GROUP BY 1 ORDER BY 2 DESC
                """);
            var agentHealth = await Counts("""
                SELECT CASE WHEN last_checkin_at IS NULL THEN 'never' WHEN last_checkin_at > now() - interval '45 minutes' THEN 'online'
                            WHEN last_checkin_at > now() - interval '7 days' THEN 'stale' ELSE 'offline' END, count(*)
                  FROM zentriq.agent_registrations WHERE status = 'active' GROUP BY 1
                """);
            var ram = await Counts("""
                SELECT CASE WHEN h.ram_gb < 4 THEN '< 4 GB' WHEN h.ram_gb < 8 THEN '4–8 GB' WHEN h.ram_gb < 16 THEN '8–16 GB' WHEN h.ram_gb < 32 THEN '16–32 GB' ELSE '32 GB +' END, count(*)
                  FROM zentriq.hardware_inventory h JOIN zentriq.assets a ON a.id = h.asset_id WHERE a.status = 'active' AND h.ram_gb IS NOT NULL GROUP BY 1
                """);
            var topSoftware = await Counts("""
                SELECT min(s.name), count(DISTINCT s.asset_id) FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id
                 WHERE a.status = 'active' GROUP BY lower(s.name) ORDER BY 2 DESC, 1 LIMIT 10
                """);
            var scanResults = await Counts("""
                SELECT r.status, count(*) FROM zentriq.scan_results r JOIN zentriq.discovery_scans s ON s.id = r.scan_id
                 WHERE s.created_at > now() - interval '30 days' AND r.status <> 'unreachable' GROUP BY 1 ORDER BY 2 DESC
                """);

            // Security posture (Windows inventory): yes / no / unknown per control.
            var posture = new List<Posture>();
            foreach (var (key, expr) in new[]
            {
                ("Disk encryption", "d.document #>> '{security,diskEncryption}'"),
                ("Firewall on", "d.document #>> '{security,firewallEnabled}'"),
                ("Secure Boot", "d.document #>> '{security,secureBoot}'"),
                ("TPM present", "d.document #>> '{hardware,tpmPresent}'"),
                ("Antivirus active", "CASE WHEN jsonb_array_length(COALESCE(d.document #> '{security,antivirus}', '[]')) = 0 THEN NULL WHEN EXISTS (SELECT 1 FROM jsonb_array_elements(d.document #> '{security,antivirus}') x WHERE x ->> 'enabled' = 'true') THEN 'true' ELSE 'false' END"),
            })
            {
                await using var cmd = new NpgsqlCommand($"""
                    SELECT count(*) FILTER (WHERE v = 'true'), count(*) FILTER (WHERE v = 'false'), count(*) FILTER (WHERE v IS NULL)
                      FROM (SELECT {expr} AS v FROM zentriq.assets a JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
                             WHERE a.status = 'active' AND a.os_family = 'windows') t
                    """, conn);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                posture.Add(new Posture(key, r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)));
            }

            var trend = new List<DayPoint>();
            await using (var cmd = new NpgsqlCommand("""
                SELECT d::date, (SELECT count(*) FROM zentriq.assets a WHERE a.first_seen_at::date = d::date),
                       (SELECT count(*) FROM zentriq.assets a WHERE a.first_seen_at < d::date + 1 AND (a.status = 'active' OR a.retired_at >= d::date + 1))
                  FROM generate_series(current_date - 29, current_date, interval '1 day') d ORDER BY 1
                """, conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) trend.Add(new DayPoint(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetInt64(1), r.GetInt64(2)));

            return Results.Ok(new
            {
                kpi, byCategory, byOs, byInventory, byCriticality, byType, byManufacturer, coverage, agentHealth, ram, topSoftware, scanResults, posture, trend,
                generatedAt = DateTimeOffset.UtcNow,
            });
        }).WithTags("Dashboards").RequireModule(Modules.Dashboards).RequireAuthorization(Permissions.AssetsView);
    }
}
