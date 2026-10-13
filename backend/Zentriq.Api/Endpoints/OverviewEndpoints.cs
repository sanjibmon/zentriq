using Npgsql;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;

namespace Zentriq.Api.Endpoints;

/// <summary>Everything the home dashboard draws, computed in one round of read-only queries.</summary>
public static class OverviewEndpoints
{
    public sealed record KeyCount(string Key, long Count);
    public sealed record Posture(string Key, long Yes, long No, long Unknown) { public string? Facet { get; init; } }
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

            async Task<List<KeyCount>> Facet(string facet, int top = 0)
            {
                var expr = AssetFacets.Expr[facet];
                var sql = $"SELECT {expr} AS k, count(*) {AssetFacets.From} WHERE a.status = 'active' AND {expr} IS NOT NULL GROUP BY 1 ORDER BY 2 DESC, 1";
                if (top > 0)
                    sql = $"SELECT CASE WHEN rn <= {top} THEN k ELSE 'Other' END, sum(n)::bigint FROM (SELECT k, n, row_number() OVER (ORDER BY n DESC, k) rn FROM ({sql.Replace("count(*)", "count(*) AS n")}) t0) t GROUP BY 1 ORDER BY 2 DESC";
                return await Counts(sql);
            }

            var facets = new Dictionary<string, List<KeyCount>>();
            foreach (var (f, top) in new[] { ("category", 0), ("os", 7), ("inventory", 0), ("type", 0), ("vendor", 7), ("coverage", 0), ("health", 0), ("seen", 0),
                                             ("ram", 0), ("cores", 0), ("cpu", 7), ("disk", 0), ("free", 0), ("criticality", 0) })
                facets[f] = await Facet(f, top);
            var byCategory = facets["category"];
            var byOs = facets["os"];
            var byInventory = facets["inventory"];
            var byCriticality = facets["criticality"];
            var byType = facets["type"];
            var byManufacturer = facets["vendor"];
            var coverage = facets["coverage"];
            var agentHealth = facets["health"];
            var ram = facets["ram"];
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
            foreach (var (key, facet) in new[] { ("Disk encryption", "encryption"), ("Firewall on", "firewall"), ("Secure Boot", "secureboot"), ("TPM present", "tpm"), ("Antivirus active", "antivirus") })
            {
                var c = await Facet(facet);
                long V(string k) => c.FirstOrDefault(x => x.Key == k)?.Count ?? 0;
                posture.Add(new Posture(key, V("yes"), V("no"), V("unknown")) { Facet = facet });
            }
            kpi["offline7"] = await Scalar($"SELECT count(*) {AssetFacets.From} WHERE a.status = 'active' AND {AssetFacets.OfflineDays} >= 7");

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
                seen = facets["seen"], cores = facets["cores"], cpu = facets["cpu"], disk = facets["disk"], free = facets["free"],
                generatedAt = DateTimeOffset.UtcNow,
            });
        }).WithTags("Dashboards").RequireModule(Modules.Dashboards).RequireAuthorization(Permissions.AssetsView);
    }
}
