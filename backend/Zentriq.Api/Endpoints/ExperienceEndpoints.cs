using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Device Experience (build 120): a score per device from what its agent reports — start-up time, application crashes
/// and hangs, blue screens, unexpected shutdowns, service failures, processor and memory pressure, disk space, battery
/// wear and uptime — and the fleet view (average, trend, the worst devices, the applications that crash most).
/// </summary>
public static class ExperienceEndpoints
{
    public static void MapExperienceEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/experience").WithTags("Device experience").RequireModule(Modules.Inventory).RequireAuthorization(Permissions.AssetsView);

        g.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await LicenseEndpoints.ScalarAsync(conn, """
                WITH x AS (SELECT e.* FROM zentriq.device_experience e JOIN zentriq.assets a ON a.id = e.asset_id AND a.status = 'active'
                            WHERE e.computed_at > now() - interval '14 days'),
                     ev AS (SELECT v.* FROM zentriq.device_experience_events v JOIN zentriq.assets a ON a.id = v.asset_id AND a.status = 'active'
                             WHERE v.at > now() - interval '7 days')
                SELECT jsonb_build_object(
                  'devices', (SELECT count(*) FROM x),
                  'score', (SELECT round(avg(score)) FROM x),
                  'good', (SELECT count(*) FROM x WHERE rating = 'good'),
                  'fair', (SELECT count(*) FROM x WHERE rating = 'fair'),
                  'poor', (SELECT count(*) FROM x WHERE rating = 'poor'),
                  'bootMs', (SELECT percentile_disc(0.5) WITHIN GROUP (ORDER BY boot_ms) FROM x WHERE boot_ms IS NOT NULL),
                  'crashes', (SELECT count(*) FROM ev WHERE kind = 'crash'),
                  'hangs', (SELECT count(*) FROM ev WHERE kind = 'hang'),
                  'bugchecks', (SELECT count(*) FROM ev WHERE kind = 'bugcheck'),
                  'bugcheckDevices', (SELECT count(DISTINCT asset_id) FROM ev WHERE kind = 'bugcheck'),
                  'trend', (SELECT COALESCE(jsonb_agg(jsonb_build_object('day', day, 'score', s, 'devices', n) ORDER BY day), '[]') FROM (
                        SELECT h.day, round(avg(h.score)) s, count(*) n FROM zentriq.device_experience_history h
                          JOIN zentriq.assets a ON a.id = h.asset_id AND a.status = 'active'
                         WHERE h.day > current_date - 30 GROUP BY h.day) t),
                  'issues', (SELECT COALESCE(jsonb_agg(jsonb_build_object('kind', k, 'devices', n) ORDER BY n DESC), '[]') FROM (
                        SELECT i->>'kind' k, count(*) n FROM x, jsonb_array_elements(x.issues) i GROUP BY 1) t),
                  'apps', (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."crashes" + t."hangs" DESC), '[]') FROM (
                        SELECT min(app) AS "app", count(*) FILTER (WHERE kind = 'crash') AS "crashes", count(*) FILTER (WHERE kind = 'hang') AS "hangs",
                               count(DISTINCT asset_id) AS "devices", max(version) AS "version", max(at) AS "last"
                          FROM ev WHERE kind IN ('crash','hang') AND app IS NOT NULL GROUP BY lower(app) ORDER BY count(*) DESC LIMIT 15) t)
                )::text
                """, null, ct);
            return Results.Text(json ?? "{}", "application/json");
        });

        g.MapGet("/devices", async (NpgsqlDataSource ds, string? rating, string? issue, string? q, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await LicenseEndpoints.ScalarAsync(conn, """
                SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."score", lower(t."hostname")), '[]')::text FROM (
                  SELECT a.id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", a.os_name AS "os", dep.name AS "department",
                         d.document #>> '{users,loggedOn}' AS "user",
                         e.score AS "score", e.rating AS "rating", e.boot_ms AS "bootMs", e.logon_ms AS "logonMs", e.crashes AS "crashes", e.hangs AS "hangs",
                         e.bugchecks AS "bugchecks", e.shutdowns AS "shutdowns", e.service_failures AS "serviceFailures",
                         e.cpu_high_percent AS "cpuHigh", e.mem_high_percent AS "memHigh", e.system_free_percent AS "systemFree",
                         e.battery_health AS "battery", e.uptime_days AS "uptimeDays", e.issues AS "issues", e.computed_at AS "computedAt"
                    FROM zentriq.device_experience e JOIN zentriq.assets a ON a.id = e.asset_id AND a.status = 'active'
                    LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
                    LEFT JOIN zentriq.departments dep ON dep.id = a.department_id
                   WHERE (@r::text IS NULL OR e.rating = @r)
                     AND (@i::text IS NULL OR e.issues @> jsonb_build_array(jsonb_build_object('kind', @i)))
                     AND (@q::text IS NULL OR a.hostname ILIKE '%' || @q || '%' OR d.document #>> '{users,loggedOn}' ILIKE '%' || @q || '%')
                   LIMIT 5000) t
                """, c =>
            {
                c.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.Text) { Value = rating is "good" or "fair" or "poor" ? rating : DBNull.Value });
                c.Parameters.Add(new NpgsqlParameter("i", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(issue) || issue.Length > 30 ? DBNull.Value : issue });
                c.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : q.Trim() });
            }, ct);
            return Results.Text(json ?? "[]", "application/json");
        });

        g.MapGet("/devices/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await LicenseEndpoints.ScalarAsync(conn, """
                SELECT jsonb_build_object(
                  'current', (SELECT to_jsonb(t) FROM (SELECT e.score AS "score", e.rating AS "rating", e.boot_ms AS "bootMs", e.logon_ms AS "logonMs",
                        e.crashes AS "crashes", e.hangs AS "hangs", e.bugchecks AS "bugchecks", e.shutdowns AS "shutdowns", e.service_failures AS "serviceFailures",
                        e.cpu_high_percent AS "cpuHigh", e.mem_high_percent AS "memHigh", e.system_free_percent AS "systemFree", e.battery_health AS "battery",
                        e.uptime_days AS "uptimeDays", e.issues AS "issues", e.computed_at AS "computedAt"
                        FROM zentriq.device_experience e WHERE e.asset_id = @a) t),
                  'history', (SELECT COALESCE(jsonb_agg(jsonb_build_object('day', day, 'score', score) ORDER BY day), '[]') FROM zentriq.device_experience_history
                        WHERE asset_id = @a AND day > current_date - 30),
                  'days', (SELECT COALESCE(jsonb_agg(jsonb_build_object('day', day, 'cpuAvg', cpu_avg, 'cpuHigh', cpu_high, 'memAvg', mem_avg, 'memHigh', mem_high, 'samples', samples) ORDER BY day), '[]')
                        FROM zentriq.device_experience_days WHERE asset_id = @a AND day > current_date - 30),
                  'events', (SELECT COALESCE(jsonb_agg(jsonb_build_object('at', at, 'kind', kind, 'app', app, 'version', version, 'detail', detail, 'ms', ms, 'ms2', ms2) ORDER BY at DESC), '[]')
                        FROM (SELECT * FROM zentriq.device_experience_events WHERE asset_id = @a AND at > now() - interval '30 days' ORDER BY at DESC LIMIT 300) v)
                )::text
                """, c => c.Parameters.AddWithValue("a", id), ct);
            return Results.Text(json ?? "{}", "application/json");
        });
    }
}
