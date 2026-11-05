using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Licenses;

/// <summary>
/// Finds the discrepancies of Software License Management and Web Apps &amp; Accounts and keeps <c>sam_alerts</c>
/// current: a new problem opens an alert (notified once), a problem that is gone resolves it. Runs in the worker every
/// 15 minutes and from the console (Evaluate now).
/// </summary>
public static class LicenseEvaluator
{
    public sealed record Alert(string Key, string Kind, string Severity, string Title, string? Detail, string? Link,
        Guid? ProductId = null, Guid? LicenseId = null, Guid? AssetId = null, Guid? AppId = null);

    public sealed record Result(int Open, int Opened, int Resolved);

    private static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static async Task<Result> RunAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        var settings = await LicenseSettings.LoadAsync(ds, ct);
        await using var conn = await ds.OpenConnectionAsync(ct);
        var alerts = new List<Alert>();

        // ---- licence position per product --------------------------------------------------------------------------
        foreach (var p in await LicenseCompliance.LoadAsync(conn, null, settings.UnusedPercent, ct))
        {
            var link = $"/modules/licenses/products/{p.Id}";
            var what = p.Kind == "cloud" ? "assigned" : p.Metric switch { "user" => "users", "core" => "cores", _ => "installed" };
            switch (p.Status)
            {
                case "shortfall":
                    alerts.Add(new Alert($"shortfall:{p.Id}", "shortfall", p.Shortfall >= 10 || p.Shortfall * 5 >= Math.Max(1, p.Entitled) ? "high" : "medium",
                        $"{p.Name}: {N(p.Consumed ?? 0)} {what}, {N(p.Entitled)} licensed — {N(p.Shortfall)} short",
                        p.ShortfallCost is { } c ? $"Estimated cost to close the gap: {c:N2}" : "Buy licences, remove installations or check the licence quantities.", link, ProductId: p.Id));
                    break;
                case "unlicensed":
                    alerts.Add(new Alert($"unlicensed:{p.Id}", "unlicensed", "medium",
                        $"{p.Name} is in use on {N(p.Consumed ?? p.Devices)} {(p.Kind == "cloud" ? "accounts" : "devices")} without a licence",
                        "Record the licences the organisation owns, or remove the software.", link, ProductId: p.Id));
                    break;
                case "over_licensed":
                    alerts.Add(new Alert($"unused:{p.Id}", "unused", "info",
                        $"{p.Name}: {N(p.Unused)} of {N(p.Entitled)} licences unused",
                        p.UnusedValue is { } v ? $"Possible saving: {v:N2}. Reassign or reduce at the next renewal." : "Reassign or reduce at the next renewal.", link, ProductId: p.Id));
                    break;
            }
        }

        // ---- subscriptions and terms ending, maintenance ending ---------------------------------------------------
        await using (var cmd = new NpgsqlCommand("""
            SELECT l.id, l.product_id, p.name, l.name, l.license_type, l.end_date, l.maintenance_end, l.auto_renew, l.notify_days, l.quantity, l.vendor
              FROM zentriq.sam_licenses l JOIN zentriq.sam_products p ON p.id = l.product_id
             WHERE l.status = 'active' AND (l.end_date IS NOT NULL OR l.maintenance_end IS NOT NULL)
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            while (await r.ReadAsync(ct))
            {
                var id = r.GetGuid(0); var pid = r.GetGuid(1); var product = r.GetString(2); var name = r.GetString(3);
                var end = r.IsDBNull(5) ? (DateOnly?)null : DateOnly.FromDateTime(r.GetDateTime(5));
                var maint = r.IsDBNull(6) ? (DateOnly?)null : DateOnly.FromDateTime(r.GetDateTime(6));
                var autoRenew = r.GetBoolean(7);
                var notify = r.GetInt32(8) > 0 ? r.GetInt32(8) : settings.ExpiryDays;
                var vendor = r.IsDBNull(10) ? null : r.GetString(10);
                var link = $"/modules/licenses/licenses/{id}";
                var label = name.Equals(product, StringComparison.OrdinalIgnoreCase) ? product : $"{product} ({name})";
                if (end is { } e)
                {
                    var days = e.DayNumber - today.DayNumber;
                    if (days < 0)
                        alerts.Add(new Alert($"expired:{id}:{D(e)}", "expired", "high", $"Licence expired: {label} ended on {D(e)}",
                            "Renew it, or retire the licence if it is no longer needed. Installations it covered now count as unlicensed.", link, pid, id));
                    else if (days <= notify)
                        alerts.Add(new Alert($"expiring:{id}:{D(e)}", "expiring", autoRenew ? "low" : days <= 7 ? "high" : "medium",
                            autoRenew ? $"Renews automatically in {days} day(s): {label} on {D(e)}" : $"Licence ends in {days} day(s): {label} on {D(e)}",
                            vendor is null ? null : $"Vendor: {vendor}", link, pid, id));
                }
                if (maint is { } m)
                {
                    var days = m.DayNumber - today.DayNumber;
                    if (days <= notify && days >= -30)
                        alerts.Add(new Alert($"maintenance:{id}:{D(m)}", "maintenance", days < 0 ? "medium" : "low",
                            days < 0 ? $"Support / maintenance ended: {label} on {D(m)}" : $"Support / maintenance ends in {days} day(s): {label} on {D(m)}",
                            "The licence stays valid; updates and support end.", link, pid, id));
                }
            }
        }

        // ---- Microsoft 365: more assigned than bought, subscriptions in grace or suspended --------------------------
        await using (var cmd = new NpgsqlCommand("""
            SELECT k.connection_id, k.sku_id, k.sku_part, COALESCE(k.name, k.sku_part), k.enabled_units, k.warning, k.suspended, k.consumed, k.status, c.name, k.next_lifecycle, k.is_trial
              FROM zentriq.sam_cloud_skus k JOIN zentriq.sam_cloud_connections c ON c.id = k.connection_id
             WHERE c.enabled
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var conn0 = r.GetGuid(0); var sku = r.GetString(1); var name = r.GetString(3);
                int enabled = r.GetInt32(4), warning = r.GetInt32(5), suspended = r.GetInt32(6), consumed = r.GetInt32(7);
                var status = r.IsDBNull(8) ? null : r.GetString(8);
                var tenant = r.GetString(9);
                var link = "/modules/licenses?tab=cloud";
                if (consumed > enabled + warning && enabled + warning > 0)
                    alerts.Add(new Alert($"cloud_overuse:{conn0}:{sku}", "cloud_overuse", "high", $"{name}: {N(consumed)} assigned, {N(enabled + warning)} available ({tenant})",
                        "More licences are assigned than bought; Microsoft can disable the extra assignments.", link));
                if (warning > 0 || status is "Warning" or "Suspended" || (suspended > 0 && consumed > 0))
                    alerts.Add(new Alert($"cloud_expiring:{conn0}:{sku}", "cloud_expiring", "high",
                        $"{name}: subscription {(status == "Suspended" || suspended > 0 ? "suspended" : "expired — in its grace period")} ({tenant})",
                        $"{N(warning + suspended)} licence(s) affected, {N(consumed)} assigned. Renew it in the Microsoft 365 admin center before the users lose access.", link));
                else if (!r.IsDBNull(10) && r.GetFieldValue<DateTimeOffset>(10) is var next && next < DateTimeOffset.UtcNow.AddDays(settings.ExpiryDays) && next > DateTimeOffset.UtcNow && enabled > 0)
                {
                    var trial = r.GetBoolean(11);
                    alerts.Add(new Alert($"cloud_expiring:{conn0}:{sku}:{next:yyyyMMdd}", "cloud_expiring", trial ? "high" : "medium",
                        $"{name}: {(trial ? "trial ends" : "subscription renews or ends")} on {next:yyyy-MM-dd} ({tenant})",
                        $"{N(enabled)} licence(s), {N(consumed)} assigned.", link));
                }
            }

        // ---- Windows and Office not activated ----------------------------------------------------------------------
        await using (var cmd = new NpgsqlCommand("""
            SELECT a.id, COALESCE(a.hostname, host(a.primary_ip)), l->>'product', l->>'status', l->>'channel', l->>'application'
              FROM zentriq.asset_inventory_documents d JOIN zentriq.assets a ON a.id = d.asset_id AND a.status = 'active',
                   jsonb_array_elements(CASE WHEN jsonb_typeof(d.document->'licenses') = 'array' THEN d.document->'licenses' ELSE '[]' END) l
             WHERE l->>'application' IN ('windows','office') AND l->>'status' NOT IN ('licensed','unknown')
             LIMIT 5000
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var asset = r.GetGuid(0); var host = r.IsDBNull(1) ? "a device" : r.GetString(1);
                var product = r.IsDBNull(2) ? "Windows" : r.GetString(2); var status = r.IsDBNull(3) ? "unknown" : r.GetString(3);
                alerts.Add(new Alert($"not_genuine:{asset}:{product.ToLowerInvariant()}", "not_genuine", status is "unlicensed" or "notification" or "non_genuine_grace" ? "medium" : "low",
                    $"{product} not activated on {host} ({StatusText(status)})",
                    r.IsDBNull(4) ? null : $"Channel: {r.GetString(4)}", $"/assets/{asset}?tab=software", AssetId: asset));
            }

        // ---- web apps: unsanctioned use, accounts of people who left, inactive accounts ----------------------------
        await using (var cmd = new NpgsqlCommand("""
            SELECT w.id, w.name, w.status, count(DISTINCT u.asset_id), count(DISTINCT NULLIF(u.user_name, ''))
              FROM zentriq.webapps w JOIN zentriq.webapp_usage u ON u.app_id = w.id AND u.last_seen > now() - interval '7 days'
             WHERE w.status IN ('unsanctioned','blocked') GROUP BY 1, 2, 3
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                alerts.Add(new Alert($"webapp_unsanctioned:{r.GetGuid(0)}", "webapp_unsanctioned", r.GetString(2) == "blocked" ? "high" : "medium",
                    $"{(r.GetString(2) == "blocked" ? "Blocked" : "Unsanctioned")} web app in use: {r.GetString(1)} on {N(r.GetInt64(3))} device(s)",
                    $"{N(r.GetInt64(4))} user(s) in the last 7 days.", $"/modules/webapps/{r.GetGuid(0)}", AppId: r.GetGuid(0)));

        await using (var cmd = new NpgsqlCommand(WebAppQueries.OrphanedSql + " LIMIT 2000", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                alerts.Add(new Alert($"webapp_orphaned:{r.GetGuid(0)}", "webapp_orphaned", r.GetString(4) is "owner" or "admin" ? "high" : "medium",
                    $"Account of {r.GetString(3)} is still active in {r.GetString(2)}", r.GetString(5), $"/modules/webapps/{r.GetGuid(1)}?tab=accounts", AppId: r.GetGuid(1)));

        await using (var cmd = new NpgsqlCommand(WebAppQueries.InactiveByAppSql, conn))
        {
            cmd.Parameters.AddWithValue("days", Math.Clamp(settings.InactiveDays, 7, 730));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (r.GetInt64(2) > 0)
                    alerts.Add(new Alert($"webapp_inactive:{r.GetGuid(0)}", "webapp_inactive", "low",
                        $"{r.GetString(1)}: {N(r.GetInt64(2))} account(s) inactive for {settings.InactiveDays} days",
                        r.IsDBNull(3) ? "Remove them or reclaim their licences." : $"Possible saving: {r.GetDecimal(3):N2} per period. Remove them or reclaim their licences.",
                        $"/modules/webapps/{r.GetGuid(0)}?tab=accounts&filter=inactive", AppId: r.GetGuid(0)));
        }

        return await StoreAsync(conn, alerts, ct);
    }

    public static string StatusText(string status) => status switch
    {
        "unlicensed" => "unlicensed", "notification" => "notification mode — not genuine or not activated", "oob_grace" => "initial grace period",
        "oot_grace" => "grace period after a hardware change", "non_genuine_grace" => "not genuine", "extended_grace" => "extended grace period", _ => status,
    };

    private static async Task<Result> StoreAsync(NpgsqlConnection conn, List<Alert> alerts, CancellationToken ct)
    {
        alerts = alerts.GroupBy(a => a.Key).Select(g => g.First()).ToList();
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lk = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('zentriq.licenses.evaluate'))", conn, tx)) await lk.ExecuteNonQueryAsync(ct);
        var opened = 0;
        foreach (var chunk in alerts.Chunk(500))
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.sam_alerts AS s (key, kind, severity, title, detail, link, product_id, license_id, asset_id, app_id)
                SELECT * FROM unnest(@k, @kd, @sv, @t, @d, @l, @p, @li, @a, @ap)
                ON CONFLICT (key) DO UPDATE SET kind = EXCLUDED.kind, severity = EXCLUDED.severity, title = EXCLUDED.title, detail = EXCLUDED.detail, link = EXCLUDED.link,
                    last_at = now(),
                    first_at = CASE WHEN s.resolved_at IS NOT NULL THEN now() ELSE s.first_at END,
                    notified_at = CASE WHEN s.resolved_at IS NOT NULL THEN NULL ELSE s.notified_at END,
                    dismissed = CASE WHEN s.resolved_at IS NOT NULL THEN false ELSE s.dismissed END,
                    resolved_at = NULL
                RETURNING s.first_at = now()
                """, conn, tx);
            cmd.Parameters.Add(Texts("k", chunk.Select(a => a.Key)));
            cmd.Parameters.Add(Texts("kd", chunk.Select(a => a.Kind)));
            cmd.Parameters.Add(Texts("sv", chunk.Select(a => a.Severity)));
            cmd.Parameters.Add(Texts("t", chunk.Select(a => Trunc(a.Title, 300))));
            cmd.Parameters.Add(Texts("d", chunk.Select(a => Trunc(a.Detail, 2000))));
            cmd.Parameters.Add(Texts("l", chunk.Select(a => a.Link)));
            cmd.Parameters.Add(Uuids("p", chunk.Select(a => a.ProductId)));
            cmd.Parameters.Add(Uuids("li", chunk.Select(a => a.LicenseId)));
            cmd.Parameters.Add(Uuids("a", chunk.Select(a => a.AssetId)));
            cmd.Parameters.Add(Uuids("ap", chunk.Select(a => a.AppId)));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) if (!r.IsDBNull(0) && r.GetBoolean(0)) opened++;
        }
        int resolved;
        await using (var cmd = new NpgsqlCommand("UPDATE zentriq.sam_alerts SET resolved_at = now() WHERE resolved_at IS NULL AND NOT (key = ANY (@k))", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = alerts.Select(a => a.Key).ToArray() });
            resolved = await cmd.ExecuteNonQueryAsync(ct);
        }
        // Resolved alerts are kept for 180 days (history).
        await using (var cmd = new NpgsqlCommand("DELETE FROM zentriq.sam_alerts WHERE resolved_at < now() - interval '180 days'", conn, tx)) await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return new Result(alerts.Count, opened, resolved);
    }

    private static string? Trunc(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private static NpgsqlParameter Texts(string name, IEnumerable<string?> values) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() };
    private static NpgsqlParameter Uuids(string name, IEnumerable<Guid?> values) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values.ToArray() };
}
