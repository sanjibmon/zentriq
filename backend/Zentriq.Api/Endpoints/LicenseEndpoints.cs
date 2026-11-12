using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Licenses;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Software License Management (software asset management): software products and how their installations are found,
/// licences (perpetual, subscription, volume, OEM, retail, enterprise, trial, freeware) with purchase and renewal details,
/// allocations to devices and users, the licence position (installed / used vs owned), Microsoft 365 cloud licences,
/// Windows and Office activation, expiry tracking and alerts.
/// </summary>
public static class LicenseEndpoints
{
    public sealed record ProductBody(string? Name, string? Publisher, string? Category, string[]? NamePatterns, string[]? ExcludePatterns, string[]? PublisherPatterns,
        string? Metric, string? Kind, bool? NeedsLicense, Guid? WebAppId, string[]? CloudSkus, string? CatalogKey, string? Notes, string[]? ExePatterns = null);
    public sealed record PreviewBody(string[]? NamePatterns, string[]? ExcludePatterns, string[]? PublisherPatterns);
    public sealed record LicenseBody(Guid? ProductId, string? Name, string? LicenseType, string? Metric, int? Quantity, bool? Unlimited, string? Edition, string? Version,
        bool? DowngradeRights, string? Vendor, string? Reseller, string? PoNumber, string? InvoiceNumber, string? ContractNumber, DateOnly? PurchaseDate,
        DateOnly? StartDate, DateOnly? EndDate, DateOnly? MaintenanceEnd, bool? AutoRenew, string? Billing, decimal? UnitCost, decimal? TotalCost, string? Currency,
        int? NotifyDays, string? LicenseKey, bool? ClearKey, Guid? DepartmentId, string? Owner, string? Status, string? Notes);
    public sealed record RenewBody(DateOnly? EndDate, int? Quantity, decimal? TotalCost, string? Note);
    public sealed record AllocateBody(Guid[]? AssetIds, string[]? Users, string? Note);
    public sealed record ConnectionBody(string? Name, string? TenantId, string? ClientId, string? ClientSecret, bool? Enabled, bool? SyncApps);
    public sealed record SettingsBody(string[]? NotifyEmails, bool? NotifyInApp, int? ExpiryDays, int? UnusedPercent, int? InactiveDays, bool? WebDiscovery, bool? Metering = null, int? UnusedDays = null, bool? ProductKeys = null);
    public sealed record ActivationKeyBody(Guid? AssetId, string? Application, string? PartialKey);
    public sealed record FromCatalogBody(string[]? Keys);

    private static readonly string[] Metrics = ["device", "user", "core", "concurrent", "site"];
    private static readonly string[] Types = ["perpetual", "subscription", "volume", "oem", "retail", "enterprise", "trial", "freeware", "open_source"];
    private static readonly string[] Billings = ["monthly", "quarterly", "annual", "multi_year", "one_time"];

    public static void MapLicenseEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/sam").WithTags("Software license management").RequireModule(Modules.Licenses).RequireAuthorization(Permissions.LicensesView);

        // ---- overview ---------------------------------------------------------------------------------------------
        g.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var rows = await LicenseCompliance.LoadAsync(conn, null, settings.UnusedPercent, ct);
            var tracked = rows.Where(r => r.NeedsLicense && r.Status != "unused").ToList();
            var json = await ScalarAsync(conn, $$"""
                SELECT jsonb_build_object(
                  'licenses', (SELECT count(*) FROM zentriq.sam_licenses WHERE status = 'active'),
                  'subscriptions', (SELECT count(*) FROM zentriq.sam_licenses WHERE status = 'active' AND license_type IN ('subscription','trial')),
                  'perpetual', (SELECT count(*) FROM zentriq.sam_licenses WHERE status = 'active' AND license_type NOT IN ('subscription','trial')),
                  'expiring30', (SELECT count(*) FROM zentriq.sam_licenses WHERE status = 'active' AND end_date BETWEEN current_date AND current_date + @days),
                  'expired', (SELECT count(*) FROM zentriq.sam_licenses WHERE status = 'active' AND end_date < current_date),
                  'maintenanceEnding', (SELECT count(*) FROM zentriq.sam_licenses WHERE status = 'active' AND maintenance_end BETWEEN current_date - 30 AND current_date + @days),
                  'spend', (SELECT COALESCE(sum(total_cost), 0) FROM zentriq.sam_licenses WHERE status = 'active'),
                  'renewalSpend', (SELECT COALESCE(sum(total_cost), 0) FROM zentriq.sam_licenses WHERE status = 'active' AND end_date BETWEEN current_date AND current_date + 90),
                  'currencies', (SELECT COALESCE(jsonb_agg(DISTINCT currency), '[]') FROM zentriq.sam_licenses WHERE status = 'active'),
                  'cloudConnections', (SELECT count(*) FROM zentriq.sam_cloud_connections),
                  'cloudFailed', (SELECT count(*) FROM zentriq.sam_cloud_connections WHERE enabled AND last_status = 'failed'),
                  'cloudAssigned', (SELECT COALESCE(sum(consumed), 0) FROM zentriq.sam_cloud_skus),
                  'cloudAvailable', (SELECT COALESCE(sum(enabled_units + warning), 0) FROM zentriq.sam_cloud_skus WHERE enabled_units < 10000),
                  'cloudDisabledLicensed', (SELECT count(*) FROM zentriq.sam_cloud_users WHERE account_enabled = false AND cardinality(skus) > 0),
                  'cloudInactiveLicensed', (SELECT count(*) FROM zentriq.sam_cloud_users WHERE account_enabled AND cardinality(skus) > 0 AND last_sign_in < now() - make_interval(days => @inactive)),
                  'notActivated', (SELECT count(DISTINCT d.asset_id) FROM zentriq.asset_inventory_documents d JOIN zentriq.assets a ON a.id = d.asset_id AND a.status = 'active',
                        jsonb_array_elements(CASE WHEN jsonb_typeof(d.document->'licenses') = 'array' THEN d.document->'licenses' ELSE '[]' END) l
                        WHERE l->>'application' IN ('windows','office') AND l->>'status' NOT IN ('licensed','unknown')),
                  'activationKnown', (SELECT count(*) FROM zentriq.asset_inventory_documents d JOIN zentriq.assets a ON a.id = d.asset_id AND a.status = 'active'
                        WHERE jsonb_typeof(d.document->'licenses') = 'array'),
                  'alerts', (SELECT COALESCE(jsonb_object_agg(severity, n), '{}') FROM (SELECT severity, count(*) n FROM zentriq.sam_alerts
                        WHERE resolved_at IS NULL AND NOT dismissed AND kind NOT LIKE 'webapp_%' GROUP BY 1) x),
                  'renewals', (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."endDate"), '[]') FROM (
                        SELECT l.id AS "id", l.name AS "name", p.name AS "product", p.id AS "productId", l.end_date AS "endDate", l.auto_renew AS "autoRenew",
                               l.license_type AS "licenseType", l.quantity AS "quantity", l.total_cost AS "totalCost", l.currency AS "currency", l.vendor AS "vendor",
                               (l.end_date - current_date) AS "days"
                          FROM zentriq.sam_licenses l JOIN zentriq.sam_products p ON p.id = l.product_id
                         WHERE l.status = 'active' AND l.end_date >= current_date - 30 ORDER BY l.end_date LIMIT 8) t)
                )::text
                """, c => { c.Parameters.AddWithValue("days", settings.ExpiryDays); c.Parameters.AddWithValue("inactive", settings.InactiveDays); }, ct);
            var extra = JsonDocument.Parse(json ?? "{}").RootElement;
            return Results.Ok(new
            {
                products = rows.Count,
                tracked = tracked.Count,
                compliant = tracked.Count(r => r.Status is "compliant" or "over_licensed"),
                shortfall = tracked.Count(r => r.Status == "shortfall"),
                unlicensed = tracked.Count(r => r.Status == "unlicensed"),
                overLicensed = tracked.Count(r => r.Status == "over_licensed"),
                notMeasured = tracked.Count(r => r.Status == "not_measured"),
                shortfallUnits = tracked.Sum(r => r.Shortfall),
                unusedUnits = tracked.Sum(r => r.Unused),
                shortfallCost = tracked.Sum(r => r.ShortfallCost ?? 0),
                unusedValue = tracked.Sum(r => r.UnusedValue ?? 0),
                compliancePercent = tracked.Count == 0 ? 100 : (int)Math.Round(100.0 * tracked.Count(r => r.Status is "compliant" or "over_licensed" or "not_measured") / tracked.Count),
                topIssues = tracked.Where(r => r.Status is "shortfall" or "unlicensed").OrderByDescending(r => r.Shortfall + (r.Status == "unlicensed" ? r.Consumed ?? 0 : 0)).Take(8),
                settings.ExpiryDays,
                detail = extra,
            });
        });

        g.MapGet("/settings", async (NpgsqlDataSource ds, CancellationToken ct) => Results.Ok(await LicenseSettings.LoadAsync(ds, ct)));

        g.MapPut("/settings", async (SettingsBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var s = await LicenseSettings.LoadAsync(ds, ct);
            if (body.NotifyEmails is not null)
            {
                var mails = body.NotifyEmails.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (mails.Count > 20 || mails.Any(m => !System.Net.Mail.MailAddress.TryCreate(m, out _))) return Validation.Problem("notifyEmails", "Enter up to 20 valid e-mail addresses.");
                s.NotifyEmails = mails;
            }
            s.NotifyInApp = body.NotifyInApp ?? s.NotifyInApp;
            if (body.ExpiryDays is { } d) { if (d is < 1 or > 365) return Validation.Problem("expiryDays", "Choose 1 to 365 days."); s.ExpiryDays = d; }
            if (body.UnusedPercent is { } u) { if (u is < 5 or > 90) return Validation.Problem("unusedPercent", "Choose 5 to 90 %."); s.UnusedPercent = u; }
            if (body.InactiveDays is { } i) { if (i is < 7 or > 730) return Validation.Problem("inactiveDays", "Choose 7 to 730 days."); s.InactiveDays = i; }
            s.WebDiscovery = body.WebDiscovery ?? s.WebDiscovery;
            s.Metering = body.Metering ?? s.Metering;
            if (body.UnusedDays is { } ud) { if (ud is < 14 or > 365) return Validation.Problem("unusedDays", "Choose 14 to 365 days."); s.UnusedDays = ud; }
            var keysOff = body.ProductKeys == false && s.ProductKeys;
            s.ProductKeys = body.ProductKeys ?? s.ProductKeys;
            await s.SaveAsync(ds, rc.UserId, ct);
            if (keysOff)
            {
                // Switched off: the keys collected so far are removed as well.
                await using var del = ds.CreateCommand("DELETE FROM zentriq.asset_product_keys");
                await del.ExecuteNonQueryAsync(ct);
            }
            await rc.AuditAsync("licenses.settings", null, detail: s);
            return Results.Ok(s);
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapPost("/evaluate", async (NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var r = await LicenseEvaluator.RunAsync(ds, ct);
            return Results.Ok(r);
        }).RequireAuthorization(Permissions.LicensesView);

        // ---- products -----------------------------------------------------------------------------------------------
        g.MapGet("/products", async (NpgsqlDataSource ds, string? status, string? q, CancellationToken ct) =>
        {
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var rows = await LicenseCompliance.LoadAsync(conn, null, settings.UnusedPercent, ct);
            var term = q?.Trim();
            return Results.Ok(rows
                .Where(r => string.IsNullOrEmpty(status) || r.Status == status || (status == "issues" && r.Status is "shortfall" or "unlicensed"))
                .Where(r => string.IsNullOrEmpty(term) || r.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || (r.Publisher ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
                            || r.Category.Contains(term, StringComparison.OrdinalIgnoreCase)));
        });

        g.MapGet("/products/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var row = (await LicenseCompliance.LoadAsync(conn, id, settings.UnusedPercent, ct)).FirstOrDefault();
            if (row is null) return Results.NotFound();
            var json = await ScalarAsync(conn, """
                SELECT jsonb_build_object(
                  'product', (SELECT jsonb_build_object('id', p.id, 'name', p.name, 'publisher', p.publisher, 'category', p.category, 'namePatterns', p.name_patterns,
                        'excludePatterns', p.exclude_patterns, 'publisherPatterns', p.publisher_patterns, 'metric', p.metric, 'kind', p.kind, 'needsLicense', p.needs_license,
                        'webAppId', p.webapp_id, 'webAppName', w.name, 'cloudSkus', p.cloud_skus, 'catalogKey', p.catalog_key, 'exePatterns', p.exe_patterns, 'notes', p.notes, 'createdBy', p.created_by,
                        'createdAt', p.created_at, 'updatedAt', p.updated_at)
                        FROM zentriq.sam_products p LEFT JOIN zentriq.webapps w ON w.id = p.webapp_id WHERE p.id = @id),
                  'licenses', (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."status", t."endDate" NULLS LAST), '[]') FROM ({{LicenseSelect}} WHERE l.product_id = @id) t),
                  'versions', (SELECT COALESCE(jsonb_agg(jsonb_build_object('version', v, 'devices', n) ORDER BY n DESC), '[]') FROM (
                        SELECT COALESCE(i.version, 'Unknown') v, count(*) n FROM (SELECT * FROM zentriq.sam_products WHERE id = @id) p0
                          CROSS JOIN LATERAL (SELECT s.asset_id, max(s.version) AS version FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id AND a.status = 'active'
                                WHERE p0.kind = 'installed' AND s.name ILIKE ANY (zentriq.sam_like(p0.name_patterns)) AND NOT (s.name ILIKE ANY (zentriq.sam_like(p0.exclude_patterns)))
                                  AND (cardinality(p0.publisher_patterns) = 0 OR s.publisher ILIKE ANY (zentriq.sam_like(p0.publisher_patterns))) GROUP BY s.asset_id) i
                         GROUP BY 1 ORDER BY 2 DESC LIMIT 20) x),
                  'cloud', (SELECT COALESCE(jsonb_agg(jsonb_build_object('connectionId', k.connection_id, 'connection', c.name, 'skuId', k.sku_id, 'skuPart', k.sku_part, 'name', k.name,
                        'enabled', k.enabled_units, 'warning', k.warning, 'suspended', k.suspended, 'consumed', k.consumed, 'status', k.status, 'nextLifecycle', k.next_lifecycle,
                        'isTrial', k.is_trial, 'syncedAt', k.synced_at)), '[]')
                        FROM zentriq.sam_cloud_skus k JOIN zentriq.sam_cloud_connections c ON c.id = k.connection_id
                       WHERE k.sku_part IN (SELECT unnest(cloud_skus) FROM zentriq.sam_products WHERE id = @id)),
                  'alerts', (SELECT COALESCE(jsonb_agg(jsonb_build_object('id', s.id, 'kind', s.kind, 'severity', s.severity, 'title', s.title, 'detail', s.detail, 'firstAt', s.first_at)
                        ORDER BY s.first_at DESC), '[]') FROM zentriq.sam_alerts s WHERE s.product_id = @id AND s.resolved_at IS NULL AND NOT s.dismissed)
                )::text
                """.Replace("{{LicenseSelect}}", LicenseSelect), c => c.Parameters.AddWithValue("id", id), ct);
            return Results.Ok(new { compliance = row, detail = JsonDocument.Parse(json ?? "{}").RootElement, unusedDays = settings.UnusedDays });
        });

        g.MapGet("/products/{id:guid}/installations", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, $$"""
                WITH p AS (SELECT * FROM zentriq.sam_products WHERE id = @id),
                {{LicenseCompliance.InstallsCte}}
                ,{{LicenseCompliance.UsageCte}}
                SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY lower(t."hostname")), '[]')::text FROM (
                  SELECT a.id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", i.software AS "software", i.version AS "version",
                         d.document #>> '{users,loggedOn}' AS "user", a.os_name AS "os", dep.name AS "department", a.last_seen_at AS "lastSeen",
                         h.cpu_cores AS "cores",
                         (SELECT l.name FROM zentriq.sam_allocations x JOIN zentriq.sam_licenses l ON l.id = x.license_id WHERE x.asset_id = a.id AND l.product_id = @id LIMIT 1) AS "allocatedTo",
                         u.last_used AS "lastUsed", COALESCE(u.minutes, 0) AS "minutesRecent", u.users AS "usedBy", m.since AS "meteredSince",
                         {{LicenseCompliance.UsageState}} AS "usage",
                         EXISTS (SELECT 1 FROM zentriq.agent_registrations r WHERE r.asset_id = a.id AND r.status = 'active' AND 'software-uninstall' = ANY (r.capabilities)) AS "canUninstall"
                    FROM inst i JOIN zentriq.assets a ON a.id = i.asset_id CROSS JOIN p
                    LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
                    LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = a.id
                    LEFT JOIN zentriq.departments dep ON dep.id = a.department_id
                    LEFT JOIN used_by u ON u.product_id = p.id AND u.asset_id = a.id
                    LEFT JOIN zentriq.software_usage_devices m ON m.asset_id = a.id
                   LIMIT 5000) t
                """, c => { c.Parameters.AddWithValue("id", id); c.Parameters.AddWithValue("days", settings.UnusedDays); }, ct);
            return Results.Text(json ?? "[]", "application/json");
        });


        // ---- software metering (build 120) -------------------------------------------------------------------------
        g.MapGet("/usage", async (NpgsqlDataSource ds, int? days, string? q, CancellationToken ct) =>
        {
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            var window = Math.Clamp(days ?? 30, 1, 400);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, $$"""
                WITH p AS (SELECT * FROM zentriq.sam_products WHERE kind = 'installed'),
                {{LicenseCompliance.InstallsCte}},
                {{LicenseCompliance.UsageCte}},
                per AS (
                  SELECT p.id, p.name, p.publisher, cardinality(p.exe_patterns) > 0 AS rules, {{LicenseCompliance.UsageState}} AS state
                    FROM inst i JOIN p ON p.id = i.product_id
                    LEFT JOIN used_by u ON u.product_id = p.id AND u.asset_id = i.asset_id
                    LEFT JOIN zentriq.software_usage_devices m ON m.asset_id = i.asset_id),
                apps AS (
                  SELECT lower(u.exe) AS key, min(u.exe) AS exe, mode() WITHIN GROUP (ORDER BY u.product) AS product, mode() WITHIN GROUP (ORDER BY u.publisher) AS publisher,
                         count(DISTINCT u.asset_id) AS devices, count(DISTINCT lower(u.user_name)) FILTER (WHERE u.user_name <> '') AS users,
                         sum(u.minutes) AS minutes, sum(u.launches) AS launches, max(u.day) AS last_used
                    FROM zentriq.software_usage u JOIN zentriq.assets a ON a.id = u.asset_id AND a.status = 'active'
                   WHERE u.day > current_date - @w
                     AND (@q::text IS NULL OR u.exe ILIKE '%' || @q || '%' OR u.product ILIKE '%' || @q || '%' OR u.publisher ILIKE '%' || @q || '%')
                   GROUP BY 1 ORDER BY sum(u.minutes) DESC LIMIT 1000)
                SELECT jsonb_build_object(
                  'metering', @metering, 'unusedDays', @days, 'days', @w,
                  'meteredDevices', (SELECT count(*) FROM zentriq.software_usage_devices m JOIN zentriq.assets a ON a.id = m.asset_id AND a.status = 'active'
                        WHERE m.reported_at > now() - interval '14 days'),
                  'apps', (SELECT COALESCE(jsonb_agg(jsonb_build_object('exe', x.exe, 'product', x.product, 'publisher', x.publisher, 'devices', x.devices, 'users', x.users,
                        'minutes', x.minutes, 'launches', x.launches, 'lastUsed', x.last_used,
                        'productId', (SELECT p.id FROM p WHERE x.exe ILIKE ANY (zentriq.sam_like(p.exe_patterns)) ORDER BY p.name LIMIT 1),
                        'productName', (SELECT p.name FROM p WHERE x.exe ILIKE ANY (zentriq.sam_like(p.exe_patterns)) ORDER BY p.name LIMIT 1))
                        ORDER BY x.minutes DESC), '[]') FROM apps x),
                  'products', (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."unused" DESC, t."name"), '[]') FROM (
                        SELECT per.id AS "id", per.name AS "name", per.publisher AS "publisher", bool_or(per.rules) AS "rules", count(*) AS "installed",
                               count(*) FILTER (WHERE state = 'used') AS "used", count(*) FILTER (WHERE state = 'unused') AS "unused",
                               count(*) FILTER (WHERE state = 'new') AS "recent", count(*) FILTER (WHERE state = 'not_metered') AS "notMetered",
                               (SELECT avg(l.unit_cost) FROM zentriq.sam_licenses l WHERE l.product_id = per.id AND l.status = 'active' AND l.unit_cost IS NOT NULL) AS "unitCost"
                          FROM per GROUP BY per.id, per.name, per.publisher) t)
                )::text
                """, c =>
            {
                c.Parameters.AddWithValue("w", window);
                c.Parameters.AddWithValue("days", settings.UnusedDays);
                c.Parameters.AddWithValue("metering", settings.Metering);
                c.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : q.Trim() });
            }, ct);
            return Results.Text(json ?? "{}", "application/json");
        });

        // Who used one program, on which devices.
        g.MapGet("/usage/program", async (NpgsqlDataSource ds, string? exe, int? days, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(exe) || exe.Length > 260) return Validation.Problem("exe", "Choose a program.");
            var window = Math.Clamp(days ?? 30, 1, 400);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, """
                SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."minutes" DESC), '[]')::text FROM (
                  SELECT a.id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", NULLIF(u.user_name, '') AS "user",
                         sum(u.minutes) AS "minutes", sum(u.launches) AS "launches", count(DISTINCT u.day) AS "daysUsed", max(u.day) AS "lastUsed",
                         max(u.version) AS "version", min(u.path) AS "path"
                    FROM zentriq.software_usage u JOIN zentriq.assets a ON a.id = u.asset_id AND a.status = 'active'
                   WHERE lower(u.exe) = lower(@e) AND u.day > current_date - @w
                   GROUP BY a.id, a.hostname, a.primary_ip, u.user_name LIMIT 5000) t
                """, c => { c.Parameters.AddWithValue("e", exe.Trim()); c.Parameters.AddWithValue("w", window); }, ct);
            return Results.Text(json ?? "[]", "application/json");
        });

        // Programs used on one device (device page).
        g.MapGet("/usage/device/{assetId:guid}", async (Guid assetId, NpgsqlDataSource ds, int? days, CancellationToken ct) =>
        {
            var window = Math.Clamp(days ?? 30, 1, 400);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, """
                SELECT jsonb_build_object(
                  'meteredSince', (SELECT since FROM zentriq.software_usage_devices WHERE asset_id = @a),
                  'programs', (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."minutes" DESC), '[]') FROM (
                    SELECT min(u.exe) AS "exe", mode() WITHIN GROUP (ORDER BY u.product) AS "product", mode() WITHIN GROUP (ORDER BY u.publisher) AS "publisher",
                           max(u.version) AS "version", sum(u.minutes) AS "minutes", sum(u.launches) AS "launches", count(DISTINCT u.day) AS "daysUsed",
                           max(u.day) AS "lastUsed", array_agg(DISTINCT NULLIF(u.user_name, '')) FILTER (WHERE u.user_name <> '') AS "users"
                      FROM zentriq.software_usage u WHERE u.asset_id = @a AND u.day > current_date - @w
                     GROUP BY lower(u.exe) LIMIT 2000) t))::text
                """, c => { c.Parameters.AddWithValue("a", assetId); c.Parameters.AddWithValue("w", window); }, ct);
            return Results.Text(json ?? "{}", "application/json");
        });

        g.MapPost("/products/preview", async (PreviewBody body, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var names = Clean(body.NamePatterns);
            if (names.Length == 0) return Validation.Problem("namePatterns", "Enter at least one name pattern.");
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, """
                SELECT COALESCE(jsonb_agg(jsonb_build_object('name', n, 'publisher', pub, 'devices', d) ORDER BY d DESC), '[]')::text FROM (
                  SELECT min(s.name) n, min(s.publisher) pub, count(DISTINCT s.asset_id) d
                    FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id AND a.status = 'active'
                   WHERE s.name ILIKE ANY (zentriq.sam_like(@n)) AND NOT (s.name ILIKE ANY (zentriq.sam_like(@x)))
                     AND (cardinality(@p::text[]) = 0 OR s.publisher ILIKE ANY (zentriq.sam_like(@p)))
                   GROUP BY lower(s.name) ORDER BY 3 DESC LIMIT 50) t
                """, c =>
            {
                c.Parameters.AddWithValue("n", names);
                c.Parameters.AddWithValue("x", Clean(body.ExcludePatterns));
                c.Parameters.AddWithValue("p", Clean(body.PublisherPatterns));
            }, ct);
            return Results.Text(json ?? "[]", "application/json");
        });

        // Suggestions: commercial software from the licence catalogue found installed, and the most installed programs not yet covered.
        g.MapGet("/discover", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var titles = new List<(string Name, string? Publisher, long Devices)>();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using (var cmd = new NpgsqlCommand("""
                SELECT min(s.name), min(s.publisher), count(DISTINCT s.asset_id)
                  FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id AND a.status = 'active'
                 WHERE NOT EXISTS (SELECT 1 FROM zentriq.sam_products p WHERE p.kind = 'installed' AND s.name ILIKE ANY (zentriq.sam_like(p.name_patterns))
                                    AND NOT (s.name ILIKE ANY (zentriq.sam_like(p.exclude_patterns))))
                 GROUP BY lower(s.name) ORDER BY 3 DESC LIMIT 4000
                """, conn) { CommandTimeout = 120 })
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) titles.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2)));
            await using (var cmd = new NpgsqlCommand("SELECT lower(name), catalog_key FROM zentriq.sam_products", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) { existing.Add(r.GetString(0)); if (!r.IsDBNull(1)) keys.Add(r.GetString(1)); }

            var suggestions = LicenseCatalog.Standard.Products
                .Where(e => !keys.Contains("catalog:" + e.Key) && !existing.Contains(e.Name))
                .Select(e => new { e.Key, e.Name, e.Publisher, e.Category, e.Metric, e.License, e.NamePatterns, e.ExcludePatterns, e.PublisherPatterns, e.ExePatterns, e.Notes,
                    matches = titles.Where(t => LicenseCatalog.Matches(e, t.Name, t.Publisher)).Take(10).Select(t => new { t.Name, t.Devices }).ToList(),
                    devices = titles.Where(t => LicenseCatalog.Matches(e, t.Name, t.Publisher)).Sum(t => t.Devices) })
                .Where(x => x.devices > 0).OrderByDescending(x => x.devices).ToList();
            return Results.Ok(new
            {
                version = LicenseCatalog.Standard.Version,
                catalog = suggestions,
                uncovered = titles.Take(200).Select(t => new { name = t.Name, publisher = t.Publisher, devices = t.Devices }),
            });
        });

        g.MapPost("/products/from-catalog", async (FromCatalogBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var entries = (body.Keys ?? []).Select(k => LicenseCatalog.Standard.Find(k)).Where(e => e is not null).Select(e => e!).ToList();
            if (entries.Count == 0) return Validation.Problem("keys", "Choose software from the list.");
            var added = 0;
            foreach (var e in entries)
            {
                await using var cmd = ds.CreateCommand("""
                    INSERT INTO zentriq.sam_products (name, publisher, category, name_patterns, exclude_patterns, publisher_patterns, metric, catalog_key, notes, created_by, exe_patterns)
                    VALUES (@n, @p, @c, @np, @xp, @pp, @m, @k, @no, @by, @ep) ON CONFLICT DO NOTHING
                    """);
                cmd.Parameters.AddWithValue("n", e.Name);
                cmd.Parameters.Add(T("p", e.Publisher));
                cmd.Parameters.AddWithValue("c", e.Category);
                cmd.Parameters.AddWithValue("np", e.NamePatterns.ToArray());
                cmd.Parameters.AddWithValue("xp", e.ExcludePatterns.ToArray());
                cmd.Parameters.AddWithValue("pp", e.PublisherPatterns.ToArray());
                cmd.Parameters.AddWithValue("m", e.Metric);
                cmd.Parameters.AddWithValue("k", "catalog:" + e.Key);
                cmd.Parameters.Add(T("no", e.Notes));
                cmd.Parameters.Add(T("by", rc.UserName));
                cmd.Parameters.AddWithValue("ep", e.ExePatterns.ToArray());
                added += await cmd.ExecuteNonQueryAsync(ct);
            }
            await rc.AuditAsync("licenses.product_add_catalog", $"{added} product(s)", detail: new { keys = entries.Select(e => e.Key) });
            return Results.Ok(new { added });
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapPost("/products", async (ProductBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var err = ValidateProduct(body);
            if (err is not null) return err;
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.sam_products (name, publisher, category, name_patterns, exclude_patterns, publisher_patterns, metric, kind, needs_license, webapp_id, cloud_skus, catalog_key, notes, created_by, exe_patterns)
                VALUES (@n, @p, @c, @np, @xp, @pp, @m, @k, @nl, @w, @cs, @ck, @no, @by, @ep) RETURNING id
                """);
            BindProduct(cmd, body);
            cmd.Parameters.Add(T("ck", body.CatalogKey));
            cmd.Parameters.Add(T("by", rc.UserName));
            try
            {
                var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
                await rc.AuditAsync("licenses.product_create", body.Name, detail: body);
                return Results.Created($"/api/v1/sam/products/{id}", new { id });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A product with this name already exists."); }
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapPut("/products/{id:guid}", async (Guid id, ProductBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var err = ValidateProduct(body);
            if (err is not null) return err;
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.sam_products SET name = @n, publisher = @p, category = @c, name_patterns = @np, exclude_patterns = @xp, publisher_patterns = @pp,
                       metric = @m, kind = @k, needs_license = @nl, webapp_id = @w, cloud_skus = @cs, notes = @no, exe_patterns = @ep, updated_at = now()
                 WHERE id = @id
                """);
            BindProduct(cmd, body);
            cmd.Parameters.AddWithValue("id", id);
            try
            {
                if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A product with this name already exists."); }
            await rc.AuditAsync("licenses.product_update", body.Name, detail: body);
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapDelete("/products/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.sam_products WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            var name = await cmd.ExecuteScalarAsync(ct) as string;
            if (name is null) return Results.NotFound();
            await rc.AuditAsync("licenses.product_delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.LicensesManage);

        // ---- licences ----------------------------------------------------------------------------------------------
        g.MapGet("/licenses", async (NpgsqlDataSource ds, string? filter, string? q, CancellationToken ct) =>
        {
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, $$"""
                SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."status", t."endDate" NULLS LAST, lower(t."product")), '[]')::text FROM ({{LicenseSelect}}
                 WHERE (@f::text IS NULL
                        OR (@f = 'active' AND l.status = 'active' AND (l.end_date IS NULL OR l.end_date >= current_date))
                        OR (@f = 'expiring' AND l.status = 'active' AND l.end_date BETWEEN current_date AND current_date + GREATEST(l.notify_days, @days))
                        OR (@f = 'expired' AND l.status = 'active' AND l.end_date < current_date)
                        OR (@f = 'subscription' AND l.license_type IN ('subscription','trial'))
                        OR (@f = 'perpetual' AND l.license_type NOT IN ('subscription','trial'))
                        OR (@f = 'cloud' AND l.cloud_sku IS NOT NULL)
                        OR (@f = 'maintenance' AND l.status = 'active' AND l.maintenance_end BETWEEN current_date - 30 AND current_date + @days)
                        OR (@f = 'retired' AND l.status = 'retired'))
                   AND (@q::text IS NULL OR l.name ILIKE '%' || @q || '%' OR p.name ILIKE '%' || @q || '%' OR l.vendor ILIKE '%' || @q || '%'
                        OR l.po_number ILIKE '%' || @q || '%' OR l.invoice_number ILIKE '%' || @q || '%' OR l.contract_number ILIKE '%' || @q || '%')
                 LIMIT 5000) t
                """, c =>
            {
                c.Parameters.Add(T("f", filter));
                c.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : q.Trim().Replace("%", "")));
                c.Parameters.AddWithValue("days", settings.ExpiryDays);
            }, ct);
            return Results.Text(json ?? "[]", "application/json");
        });

        g.MapGet("/licenses/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, $$"""
                SELECT jsonb_build_object(
                  'license', (SELECT to_jsonb(t) FROM ({{LicenseSelect}} WHERE l.id = @id) t),
                  'allocations', (SELECT COALESCE(jsonb_agg(jsonb_build_object('id', x.id, 'assetId', x.asset_id, 'assetName', COALESCE(a.hostname, host(a.primary_ip)),
                        'user', x.user_name, 'note', x.note, 'createdBy', x.created_by, 'createdAt', x.created_at) ORDER BY x.created_at DESC), '[]')
                        FROM zentriq.sam_allocations x LEFT JOIN zentriq.assets a ON a.id = x.asset_id WHERE x.license_id = @id),
                  'alerts', (SELECT COALESCE(jsonb_agg(jsonb_build_object('id', s.id, 'kind', s.kind, 'severity', s.severity, 'title', s.title, 'firstAt', s.first_at)), '[]')
                        FROM zentriq.sam_alerts s WHERE s.license_id = @id AND s.resolved_at IS NULL AND NOT s.dismissed)
                )::text
                """, c => c.Parameters.AddWithValue("id", id), ct);
            var doc = JsonDocument.Parse(json ?? "{}").RootElement;
            return doc.TryGetProperty("license", out var l) && l.ValueKind == JsonValueKind.Object ? Results.Text(json!, "application/json") : Results.NotFound();
        });

        g.MapPost("/licenses", async (LicenseBody body, NpgsqlDataSource ds, CredentialProtector protector, RequestContext rc, CancellationToken ct) =>
        {
            var err = ValidateLicense(body, true);
            if (err is not null) return err;
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand($"""
                INSERT INTO zentriq.sam_licenses (id, product_id, {LicenseColumns}, key_hint, key_protected, created_by)
                VALUES (@id, @pid, {LicenseValues}, @kh, @kp, @by)
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("pid", body.ProductId!.Value);
            BindLicense(cmd, body);
            var key = string.IsNullOrWhiteSpace(body.LicenseKey) ? null : body.LicenseKey.Trim();
            cmd.Parameters.Add(T("kh", key is null ? null : KeyHint(key)));
            cmd.Parameters.Add(T("kp", key is null ? null : M365Sync.Protect(protector, id, key)));
            cmd.Parameters.Add(T("by", rc.UserName));
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation) { return Validation.Problem("productId", "Choose the software product."); }
            await rc.AuditAsync("licenses.license_create", body.Name, detail: Redacted(body));
            return Results.Created($"/api/v1/sam/licenses/{id}", new { id });
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapPut("/licenses/{id:guid}", async (Guid id, LicenseBody body, NpgsqlDataSource ds, CredentialProtector protector, RequestContext rc, CancellationToken ct) =>
        {
            var err = ValidateLicense(body, false);
            if (err is not null) return err;
            var key = string.IsNullOrWhiteSpace(body.LicenseKey) ? null : body.LicenseKey.Trim();
            var setKey = key is not null ? ", key_hint = @kh, key_protected = @kp" : body.ClearKey == true ? ", key_hint = NULL, key_protected = NULL" : "";
            await using var cmd = ds.CreateCommand($"""
                UPDATE zentriq.sam_licenses SET ({LicenseColumns}) = ROW({LicenseValues}), product_id = COALESCE(@pid, product_id), updated_at = now(){setKey}
                 WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.Add(new NpgsqlParameter("pid", NpgsqlDbType.Uuid) { Value = (object?)body.ProductId ?? DBNull.Value });
            BindLicense(cmd, body);
            if (key is not null)
            {
                cmd.Parameters.AddWithValue("kh", KeyHint(key));
                cmd.Parameters.AddWithValue("kp", M365Sync.Protect(protector, id, key));
            }
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await rc.AuditAsync("licenses.license_update", body.Name, detail: Redacted(body));
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapPost("/licenses/{id:guid}/renew", async (Guid id, RenewBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (body.EndDate is null) return Validation.Problem("endDate", "Enter the new end date.");
            if (body.Quantity is < 0) return Validation.Problem("quantity", "The quantity cannot be negative.");
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.sam_licenses SET end_date = @e, quantity = COALESCE(@q, quantity), total_cost = COALESCE(@c, total_cost), status = 'active',
                       notes = CASE WHEN @n::text IS NULL THEN notes ELSE concat_ws(E'\n', notes, to_char(now(), 'YYYY-MM-DD') || ' renewed: ' || @n) END, updated_at = now()
                 WHERE id = @id AND (start_date IS NULL OR start_date <= @e) RETURNING name
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("e", body.EndDate.Value);
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Integer) { Value = (object?)body.Quantity ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Numeric) { Value = (object?)body.TotalCost ?? DBNull.Value });
            cmd.Parameters.Add(T("n", string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim()));
            var name = await cmd.ExecuteScalarAsync(ct) as string;
            if (name is null) return Validation.Problem("endDate", "The licence was not found, or the end date is before its start.");
            await rc.AuditAsync("licenses.license_renew", name, detail: body);
            await LicenseEvaluator.RunAsync(ds, ct);
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapPost("/licenses/{id:guid}/key", async (Guid id, NpgsqlDataSource ds, CredentialProtector protector, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT name, key_protected FROM zentriq.sam_licenses WHERE id = @id");
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            var name = r.GetString(0);
            if (r.IsDBNull(1)) return Results.Problem(statusCode: 404, title: "No licence key is stored for this licence.");
            var key = protector.Unprotect(id, r.GetString(1)).Password;
            await rc.AuditAsync("licenses.key_view", name);
            return Results.Ok(new { key });
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapDelete("/licenses/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.sam_licenses WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            var name = await cmd.ExecuteScalarAsync(ct) as string;
            if (name is null) return Results.NotFound();
            await rc.AuditAsync("licenses.license_delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapPost("/licenses/{id:guid}/allocations", async (Guid id, AllocateBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var assets = (body.AssetIds ?? []).Distinct().Take(5000).ToArray();
            var users = (body.Users ?? []).Select(u => u.Trim()).Where(u => u.Length is > 0 and <= 254).Distinct(StringComparer.OrdinalIgnoreCase).Take(5000).ToArray();
            if (assets.Length == 0 && users.Length == 0) return Validation.Problem("assetIds", "Choose devices or enter users.");
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.sam_allocations (license_id, asset_id, note, created_by) SELECT @l, a, @n, @by FROM unnest(@a::uuid[]) a
                 WHERE EXISTS (SELECT 1 FROM zentriq.assets x WHERE x.id = a) ON CONFLICT DO NOTHING;
                INSERT INTO zentriq.sam_allocations (license_id, user_name, note, created_by) SELECT @l, u, @n, @by FROM unnest(@u::text[]) u ON CONFLICT DO NOTHING;
                """);
            cmd.Parameters.AddWithValue("l", id);
            cmd.Parameters.AddWithValue("a", assets);
            cmd.Parameters.AddWithValue("u", users);
            cmd.Parameters.Add(T("n", string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim()));
            cmd.Parameters.Add(T("by", rc.UserName));
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation) { return Results.NotFound(); }
            await rc.AuditAsync("licenses.allocate", id.ToString(), detail: new { assets = assets.Length, users = users.Length });
            return Results.Ok(new { assets = assets.Length, users = users.Length });
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapDelete("/allocations/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.sam_allocations WHERE id = @id");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await rc.AuditAsync("licenses.deallocate", id.ToString());
            return Results.NoContent();
        }).RequireAuthorization(Permissions.LicensesManage);

        // ---- Windows / Office activation ---------------------------------------------------------------------------
        g.MapGet("/activation", async (NpgsqlDataSource ds, string? filter, string? q, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, """
                SELECT jsonb_build_object(
                  'rows', (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."activated", lower(t."hostname")), '[]') FROM (
                    SELECT a.id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", a.os_name AS "os", dep.name AS "department",
                           l->>'product' AS "product", l->>'application' AS "application", l->>'channel' AS "channel", l->>'status' AS "status",
                           l->>'partialKey' AS "partialKey", (l->>'graceMinutes')::bigint AS "graceMinutes", l->>'description' AS "description",
                           (l->>'status' IN ('licensed','embedded')) AS "activated", d.collected_at AS "collectedAt",
                           EXISTS (SELECT 1 FROM zentriq.asset_product_keys k WHERE k.asset_id = a.id AND k.application = l->>'application'
                                     AND (l->>'partialKey' IS NULL OR k.key_hint = upper(l->>'partialKey'))) AS "hasKey"
                      FROM zentriq.asset_inventory_documents d JOIN zentriq.assets a ON a.id = d.asset_id AND a.status = 'active'
                      LEFT JOIN zentriq.departments dep ON dep.id = a.department_id,
                           jsonb_array_elements(CASE WHEN jsonb_typeof(d.document->'licenses') = 'array' THEN d.document->'licenses' ELSE '[]' END) l
                     WHERE (@f::text IS NULL OR (@f = 'problems' AND l->>'application' IN ('windows','office') AND l->>'status' NOT IN ('licensed','unknown'))
                            OR (@f = 'windows' AND l->>'application' = 'windows') OR (@f = 'office' AND l->>'application' = 'office') OR (@f = 'firmware' AND l->>'application' = 'firmware'))
                       AND (@q::text IS NULL OR a.hostname ILIKE '%' || @q || '%' OR l->>'product' ILIKE '%' || @q || '%' OR l->>'channel' ILIKE '%' || @q || '%')
                     LIMIT 10000) t),
                  'channels', (SELECT COALESCE(jsonb_object_agg(ch, n), '{}') FROM (
                    SELECT COALESCE(l->>'channel', 'Unknown') ch, count(*) n FROM zentriq.asset_inventory_documents d JOIN zentriq.assets a ON a.id = d.asset_id AND a.status = 'active',
                           jsonb_array_elements(CASE WHEN jsonb_typeof(d.document->'licenses') = 'array' THEN d.document->'licenses' ELSE '[]' END) l
                     WHERE l->>'application' = 'windows' GROUP BY 1) x)
                )::text
                """, c => { c.Parameters.Add(T("f", filter)); c.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : q.Trim().Replace("%", ""))); }, ct);
            return Results.Text(json ?? "{}", "application/json");
        });

        // The full key behind a Windows / Office / firmware activation row, where the device keeps one.
        g.MapPost("/activation/key", async (ActivationKeyBody body, NpgsqlDataSource ds, CredentialProtector protector, RequestContext rc, CancellationToken ct) =>
        {
            if (body.AssetId is not { } assetId || body.Application is not ("windows" or "office" or "firmware")) return Validation.Problem("application", "Choose a Windows, Office or firmware licence.");
            await using var cmd = ds.CreateCommand("""
                SELECT k.id, k.key_protected, k.product, COALESCE(a.hostname, host(a.primary_ip)), k.note
                  FROM zentriq.asset_product_keys k JOIN zentriq.assets a ON a.id = k.asset_id
                 WHERE k.asset_id = @a AND k.application = @app AND (@pk::text IS NULL OR k.key_hint = upper(@pk))
                 ORDER BY k.last_seen_at DESC LIMIT 1
                """);
            cmd.Parameters.AddWithValue("a", assetId);
            cmd.Parameters.AddWithValue("app", body.Application);
            cmd.Parameters.Add(T("pk", string.IsNullOrWhiteSpace(body.PartialKey) ? null : body.PartialKey.Trim()));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return Results.Problem(statusCode: 404, title: "The device does not keep this key in full — only its last five characters. That is the case for Office 2013 and later, Microsoft 365 and volume-activated Windows.");
            var key = protector.Unprotect(r.GetGuid(0), r.GetString(1)).Password;
            await rc.AuditAsync("licenses.device_key_view", r.GetString(2), detail: new { device = r.IsDBNull(3) ? null : r.GetString(3) });
            return Results.Ok(new { key, note = r.IsDBNull(4) ? null : r.GetString(4) });
        }).RequireAuthorization(Permissions.LicensesManage);

        // ---- product keys found on devices ------------------------------------------------------------------------
        g.MapGet("/keys", async (NpgsqlDataSource ds, string? application, string? q, Guid? assetId, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, """
                SELECT jsonb_build_object(
                  'enabled', COALESCE((SELECT (value->>'productKeys')::boolean FROM zentriq.system_settings WHERE key = 'licenses.settings'), true),
                  'rows', (SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY lower(t."product"), lower(t."hostname")), '[]') FROM (
                    SELECT k.id AS "id", a.id AS "assetId", COALESCE(a.hostname, host(a.primary_ip)) AS "hostname", dep.name AS "department", k.product AS "product",
                           k.publisher AS "publisher", k.application AS "application", k.source AS "source", k.key_hint AS "keyHint", k.note AS "note",
                           k.first_seen_at AS "firstSeenAt", k.last_seen_at AS "lastSeenAt"
                      FROM zentriq.asset_product_keys k JOIN zentriq.assets a ON a.id = k.asset_id AND a.status = 'active'
                      LEFT JOIN zentriq.departments dep ON dep.id = a.department_id
                     WHERE (@app::text IS NULL OR k.application = @app OR (@app = 'microsoft' AND k.application <> 'other'))
                       AND (@a::uuid IS NULL OR k.asset_id = @a)
                       AND (@q::text IS NULL OR a.hostname ILIKE '%' || @q || '%' OR k.product ILIKE '%' || @q || '%' OR k.publisher ILIKE '%' || @q || '%' OR k.key_hint ILIKE '%' || @q || '%')
                     LIMIT 10000) t))::text
                """, c =>
            {
                c.Parameters.Add(T("app", string.IsNullOrWhiteSpace(application) ? null : application));
                c.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : q.Trim().Replace("%", "")));
                c.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)assetId ?? DBNull.Value });
            }, ct);
            return Results.Text(json ?? "{}", "application/json");
        });

        g.MapPost("/keys/{id:guid}/reveal", async (Guid id, NpgsqlDataSource ds, CredentialProtector protector, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT k.key_protected, k.product, COALESCE(a.hostname, host(a.primary_ip))
                  FROM zentriq.asset_product_keys k JOIN zentriq.assets a ON a.id = k.asset_id WHERE k.id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            var key = protector.Unprotect(id, r.GetString(0)).Password;
            await rc.AuditAsync("licenses.device_key_view", r.GetString(1), detail: new { device = r.IsDBNull(2) ? null : r.GetString(2) });
            return Results.Ok(new { key });
        }).RequireAuthorization(Permissions.LicensesManage);

        // ---- software found in the inventory, to pick from when adding a product or a licence ---------------------
        g.MapGet("/inventory/software", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, """
                SELECT jsonb_build_object(
                  'software', (SELECT COALESCE(jsonb_agg(jsonb_build_object('name', n, 'publisher', pub, 'devices', d) ORDER BY lower(n)), '[]') FROM (
                    SELECT min(s.name) n, min(s.publisher) pub, count(DISTINCT s.asset_id) d
                      FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id AND a.status = 'active'
                     GROUP BY lower(s.name) ORDER BY 3 DESC LIMIT 5000) t),
                  'publishers', (SELECT COALESCE(jsonb_agg(jsonb_build_object('name', pub, 'devices', d) ORDER BY lower(pub)), '[]') FROM (
                    SELECT min(btrim(s.publisher)) pub, count(DISTINCT s.asset_id) d
                      FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id AND a.status = 'active'
                     WHERE btrim(COALESCE(s.publisher, '')) <> ''
                     GROUP BY lower(btrim(s.publisher)) ORDER BY 2 DESC LIMIT 2000) t))::text
                """, null, ct);
            return Results.Text(json ?? "{}", "application/json");
        });

        // Versions (and editions in the names) installed of a product, or of software by name when the product is not saved yet.
        g.MapGet("/inventory/versions", async (NpgsqlDataSource ds, Guid? productId, string? name, CancellationToken ct) =>
        {
            if (productId is null && string.IsNullOrWhiteSpace(name)) return Results.Ok(Array.Empty<object>());
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, """
                SELECT COALESCE(jsonb_agg(jsonb_build_object('version', v, 'devices', d) ORDER BY d DESC, v DESC), '[]')::text FROM (
                  SELECT btrim(s.version) v, count(DISTINCT s.asset_id) d
                    FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id AND a.status = 'active'
                   WHERE btrim(COALESCE(s.version, '')) <> ''
                     AND ((@p::uuid IS NOT NULL AND EXISTS (SELECT 1 FROM zentriq.sam_products p WHERE p.id = @p AND s.name ILIKE ANY (zentriq.sam_like(p.name_patterns))
                              AND NOT (s.name ILIKE ANY (zentriq.sam_like(p.exclude_patterns)))))
                       OR (@p::uuid IS NULL AND lower(s.name) = lower(@n)))
                   GROUP BY btrim(s.version) ORDER BY 2 DESC LIMIT 300) t
                """, c =>
            {
                c.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Uuid) { Value = (object?)productId ?? DBNull.Value });
                c.Parameters.Add(T("n", string.IsNullOrWhiteSpace(name) ? null : name.Trim()));
            }, ct);
            return Results.Text(json ?? "[]", "application/json");
        });

        // ---- Microsoft 365 / cloud licences ------------------------------------------------------------------------
        g.MapGet("/cloud", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var inactiveDays = (await LicenseSettings.LoadAsync(ds, ct)).InactiveDays;
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, """
                SELECT jsonb_build_object(
                  'connections', (SELECT COALESCE(jsonb_agg(jsonb_build_object('id', c.id, 'name', c.name, 'provider', c.provider, 'tenantId', c.tenant_id, 'clientId', c.client_id,
                        'hasSecret', c.client_protected IS NOT NULL, 'enabled', c.enabled, 'syncApps', c.sync_apps, 'lastSyncAt', c.last_sync_at, 'lastStatus', c.last_status,
                        'lastError', c.last_error, 'lastNote', c.last_note,
                        'users', (SELECT count(*) FROM zentriq.sam_cloud_users u WHERE u.connection_id = c.id),
                        'skus', (SELECT count(*) FROM zentriq.sam_cloud_skus k WHERE k.connection_id = c.id)) ORDER BY c.name), '[]') FROM zentriq.sam_cloud_connections c),
                  'skus', (SELECT COALESCE(jsonb_agg(jsonb_build_object('connectionId', k.connection_id, 'connection', c.name, 'skuId', k.sku_id, 'skuPart', k.sku_part, 'name', k.name,
                        'enabled', k.enabled_units, 'warning', k.warning, 'suspended', k.suspended, 'consumed', k.consumed, 'available', GREATEST(k.enabled_units + k.warning - k.consumed, 0),
                        'status', k.status, 'nextLifecycle', k.next_lifecycle, 'isTrial', k.is_trial, 'syncedAt', k.synced_at,
                        'disabledAssigned', (SELECT count(*) FROM zentriq.sam_cloud_users u WHERE u.connection_id = k.connection_id AND k.sku_id = ANY (u.skus) AND u.account_enabled = false),
                        'inactiveAssigned', (SELECT count(*) FROM zentriq.sam_cloud_users u WHERE u.connection_id = k.connection_id AND k.sku_id = ANY (u.skus) AND u.account_enabled
                                             AND u.last_sign_in < now() - make_interval(days => @inactive)),
                        'productId', (SELECT p.id FROM zentriq.sam_products p WHERE k.sku_part = ANY (p.cloud_skus) LIMIT 1))
                        ORDER BY k.consumed DESC), '[]')
                        FROM zentriq.sam_cloud_skus k JOIN zentriq.sam_cloud_connections c ON c.id = k.connection_id)
                )::text
                """, c => c.Parameters.AddWithValue("inactive", inactiveDays), ct);
            return Results.Text(json ?? "{}", "application/json");
        });

        g.MapGet("/cloud/users", async (NpgsqlDataSource ds, Guid? connectionId, string? skuId, string? filter, string? q, CancellationToken ct) =>
        {
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, """
                SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY lower(t."upn")), '[]')::text FROM (
                  SELECT u.connection_id AS "connectionId", u.user_id AS "userId", u.upn AS "upn", u.display_name AS "displayName", u.mail AS "mail", u.department AS "department",
                         u.account_enabled AS "enabled", u.last_sign_in AS "lastSignIn",
                         (SELECT COALESCE(array_agg(COALESCE(k.name, k.sku_part) ORDER BY k.name), '{}') FROM zentriq.sam_cloud_skus k WHERE k.connection_id = u.connection_id AND k.sku_id = ANY (u.skus)) AS "licenses"
                    FROM zentriq.sam_cloud_users u
                   WHERE (@c::uuid IS NULL OR u.connection_id = @c) AND (@s::text IS NULL OR @s = ANY (u.skus))
                     AND (@f::text IS NULL OR (@f = 'licensed' AND cardinality(u.skus) > 0)
                          OR (@f = 'disabled' AND u.account_enabled = false AND cardinality(u.skus) > 0)
                          OR (@f = 'inactive' AND u.account_enabled AND cardinality(u.skus) > 0 AND u.last_sign_in < now() - make_interval(days => @days))
                          OR (@f = 'unlicensed' AND cardinality(u.skus) = 0 AND u.account_enabled))
                     AND (@q::text IS NULL OR u.upn ILIKE '%' || @q || '%' OR u.display_name ILIKE '%' || @q || '%' OR u.department ILIKE '%' || @q || '%')
                   LIMIT 5000) t
                """, c =>
            {
                c.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Uuid) { Value = (object?)connectionId ?? DBNull.Value });
                c.Parameters.Add(T("s", skuId)); c.Parameters.Add(T("f", filter));
                c.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : q.Trim().Replace("%", "")));
                c.Parameters.AddWithValue("days", settings.InactiveDays);
            }, ct);
            return Results.Text(json ?? "[]", "application/json");
        });

        g.MapPost("/cloud/connections", async (ConnectionBody body, NpgsqlDataSource ds, CredentialProtector protector, M365Sync sync, RequestContext rc, CancellationToken ct) =>
        {
            var err = ValidateConnection(body, true);
            if (err is not null) return err;
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.sam_cloud_connections (id, name, tenant_id, client_id, client_protected, enabled, sync_apps, created_by, last_status)
                VALUES (@id, @n, @t, @c, @s, @e, @a, @by, 'requested')
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", body.Name!.Trim());
            cmd.Parameters.AddWithValue("t", body.TenantId!.Trim());
            cmd.Parameters.AddWithValue("c", body.ClientId!.Trim());
            cmd.Parameters.AddWithValue("s", M365Sync.Protect(protector, id, body.ClientSecret!.Trim()));
            cmd.Parameters.AddWithValue("e", body.Enabled ?? true);
            cmd.Parameters.AddWithValue("a", body.SyncApps ?? true);
            cmd.Parameters.Add(T("by", rc.UserName));
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A connection with this name already exists."); }
            await rc.AuditAsync("licenses.cloud_create", body.Name, detail: new { body.TenantId, body.ClientId, body.SyncApps });
            if (body.Enabled ?? true) StartSync(sync, id);
            return Results.Created($"/api/v1/sam/cloud/connections/{id}", new { id });
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapPut("/cloud/connections/{id:guid}", async (Guid id, ConnectionBody body, NpgsqlDataSource ds, CredentialProtector protector, RequestContext rc, CancellationToken ct) =>
        {
            var err = ValidateConnection(body, false);
            if (err is not null) return err;
            var setSecret = string.IsNullOrWhiteSpace(body.ClientSecret) ? "" : ", client_protected = @s";
            await using var cmd = ds.CreateCommand($"""
                UPDATE zentriq.sam_cloud_connections SET name = @n, tenant_id = @t, client_id = @c, enabled = @e, sync_apps = @a, last_status = 'requested'{setSecret} WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", body.Name!.Trim());
            cmd.Parameters.AddWithValue("t", body.TenantId!.Trim());
            cmd.Parameters.AddWithValue("c", body.ClientId!.Trim());
            cmd.Parameters.AddWithValue("e", body.Enabled ?? true);
            cmd.Parameters.AddWithValue("a", body.SyncApps ?? true);
            if (setSecret.Length > 0) cmd.Parameters.AddWithValue("s", M365Sync.Protect(protector, id, body.ClientSecret!.Trim()));
            try { if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A connection with this name already exists."); }
            await rc.AuditAsync("licenses.cloud_update", body.Name, detail: new { body.TenantId, body.ClientId, body.SyncApps, body.Enabled, secretChanged = setSecret.Length > 0 });
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapPost("/cloud/connections/{id:guid}/sync", async (Guid id, NpgsqlDataSource ds, M365Sync sync, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("UPDATE zentriq.sam_cloud_connections SET last_status = 'requested' WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            var name = await cmd.ExecuteScalarAsync(ct) as string;
            if (name is null) return Results.NotFound();
            StartSync(sync, id);
            await rc.AuditAsync("licenses.cloud_sync", name);
            return Results.Accepted();
        }).RequireAuthorization(Permissions.LicensesManage);

        g.MapDelete("/cloud/connections/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.sam_licenses SET status = 'retired', synced = false, updated_at = now() WHERE cloud_connection_id = @id AND synced;
                DELETE FROM zentriq.sam_cloud_connections WHERE id = @id RETURNING name;
                """);
            cmd.Parameters.AddWithValue("id", id);
            var name = await cmd.ExecuteScalarAsync(ct) as string;
            if (name is null) return Results.NotFound();
            await rc.AuditAsync("licenses.cloud_delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.LicensesManage);

        // ---- alerts ------------------------------------------------------------------------------------------------
        g.MapGet("/alerts", async (NpgsqlDataSource ds, string? scope, string? status, string? kind, CancellationToken ct) =>
            Results.Text(await AlertsJsonAsync(ds, scope ?? "licenses", status, kind, ct), "application/json"));

        g.MapPost("/alerts/{id:long}/dismiss", async (long id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => await DismissAsync(ds, rc, id, true, ct))
            .RequireAuthorization(Permissions.LicensesManage);
        g.MapPost("/alerts/{id:long}/restore", async (long id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => await DismissAsync(ds, rc, id, false, ct))
            .RequireAuthorization(Permissions.LicensesManage);

        // ---- dashboard highlights (the areas the user can see and the license covers) -----------------------------
        api.MapGet("/sam/highlights", async (NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var status = await rc.LicenseAsync(ct);
            var lic = rc.Has(Permissions.LicensesView) && status.ModuleEnabled(Modules.Licenses);
            var web = rc.Has(Permissions.WebAppsView) && status.ModuleEnabled(Modules.WebApps);
            if (!lic && !web) return Results.Ok(new { licenses = (object?)null, webapps = (object?)null });
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            object? licenses = null, webapps = null;
            if (lic)
            {
                var rows = (await LicenseCompliance.LoadAsync(conn, null, settings.UnusedPercent, ct)).Where(r => r.NeedsLicense && r.Status != "unused").ToList();
                var json = await ScalarAsync(conn, """
                    SELECT jsonb_build_object(
                      'expiring', (SELECT count(*) FROM zentriq.sam_licenses WHERE status = 'active' AND end_date BETWEEN current_date AND current_date + GREATEST(notify_days, @days)),
                      'expired', (SELECT count(*) FROM zentriq.sam_licenses WHERE status = 'active' AND end_date < current_date),
                      'cloudIssues', (SELECT count(*) FROM zentriq.sam_alerts WHERE resolved_at IS NULL AND NOT dismissed AND kind IN ('cloud_overuse','cloud_expiring')),
                      'notActivated', (SELECT count(*) FROM zentriq.sam_alerts WHERE resolved_at IS NULL AND NOT dismissed AND kind = 'not_genuine'),
                      'next', (SELECT jsonb_build_object('name', p.name, 'endDate', l.end_date, 'days', l.end_date - current_date) FROM zentriq.sam_licenses l
                                 JOIN zentriq.sam_products p ON p.id = l.product_id WHERE l.status = 'active' AND l.end_date >= current_date ORDER BY l.end_date LIMIT 1))::text
                    """, c => c.Parameters.AddWithValue("days", settings.ExpiryDays), ct);
                licenses = new
                {
                    products = rows.Count,
                    shortfall = rows.Count(r => r.Status == "shortfall"),
                    unlicensed = rows.Count(r => r.Status == "unlicensed"),
                    compliancePercent = rows.Count == 0 ? 100 : (int)Math.Round(100.0 * rows.Count(r => r.Status is "compliant" or "over_licensed" or "not_measured") / rows.Count),
                    detail = JsonDocument.Parse(json ?? "{}").RootElement,
                };
            }
            if (web)
            {
                var json = await ScalarAsync(conn, $$"""
                    WITH {{WebAppQueries.AccountsCte}}
                    SELECT jsonb_build_object(
                      'apps', (SELECT count(*) FROM zentriq.webapps),
                      'review', (SELECT count(*) FROM zentriq.webapps WHERE status = 'review'),
                      'unsanctionedInUse', (SELECT count(DISTINCT w.id) FROM zentriq.webapps w JOIN zentriq.webapp_usage u ON u.app_id = w.id AND u.last_seen > now() - interval '7 days'
                                            WHERE w.status IN ('unsanctioned','blocked')),
                      'accounts', (SELECT count(*) FROM accx WHERE status = 'active'),
                      'orphaned', (SELECT count(*) FROM accx WHERE orphaned),
                      'inactive', (SELECT count(*) FROM accx WHERE inactive))::text
                    """, c => c.Parameters.AddWithValue("days", settings.InactiveDays), ct);
                webapps = JsonDocument.Parse(json ?? "{}").RootElement;
            }
            return Results.Ok(new { licenses, webapps });
        }).RequireAuthorization().WithTags("Software license management");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------
    public const string LicenseSelect = """
        SELECT l.id AS "id", l.product_id AS "productId", p.name AS "product", p.publisher AS "publisher", l.name AS "name", l.license_type AS "licenseType",
               COALESCE(l.metric, p.metric) AS "metric", l.metric IS NULL AS "metricFromProduct", l.quantity AS "quantity",
               CASE WHEN l.cloud_sku IS NOT NULL AND l.quantity IS NULL THEN k.enabled_units + k.warning END AS "cloudQuantity",
               k.consumed AS "cloudConsumed", l.edition AS "edition", l.version AS "version", l.downgrade_rights AS "downgradeRights",
               l.vendor AS "vendor", l.reseller AS "reseller", l.po_number AS "poNumber", l.invoice_number AS "invoiceNumber", l.contract_number AS "contractNumber",
               l.purchase_date AS "purchaseDate", l.start_date AS "startDate", l.end_date AS "endDate", l.maintenance_end AS "maintenanceEnd",
               l.auto_renew AS "autoRenew", l.billing AS "billing", l.unit_cost AS "unitCost", l.total_cost AS "totalCost", l.currency AS "currency",
               l.notify_days AS "notifyDays", l.key_hint AS "keyHint", l.key_protected IS NOT NULL AS "hasKey", l.department_id AS "departmentId", dep.name AS "department",
               l.owner AS "owner", l.cloud_connection_id AS "cloudConnectionId", c.name AS "cloudConnection", l.cloud_sku AS "cloudSku", l.synced AS "synced",
               l.notes AS "notes", l.created_by AS "createdBy", l.created_at AS "createdAt", l.updated_at AS "updatedAt",
               (SELECT count(*) FROM zentriq.sam_allocations x WHERE x.license_id = l.id) AS "allocations",
               (l.end_date - current_date) AS "daysLeft",
               CASE WHEN l.status = 'retired' THEN 'retired' WHEN l.end_date < current_date THEN 'expired'
                    WHEN l.end_date <= current_date + l.notify_days THEN 'expiring' ELSE 'active' END AS "status"
          FROM zentriq.sam_licenses l
          JOIN zentriq.sam_products p ON p.id = l.product_id
          LEFT JOIN zentriq.departments dep ON dep.id = l.department_id
          LEFT JOIN zentriq.sam_cloud_connections c ON c.id = l.cloud_connection_id
          LEFT JOIN zentriq.sam_cloud_skus k ON k.connection_id = l.cloud_connection_id AND k.sku_part = l.cloud_sku
        """;

    private const string LicenseColumns = "name, license_type, metric, quantity, edition, version, downgrade_rights, vendor, reseller, po_number, invoice_number, contract_number, " +
                                          "purchase_date, start_date, end_date, maintenance_end, auto_renew, billing, unit_cost, total_cost, currency, notify_days, department_id, owner, status, notes";
    private const string LicenseValues = "@n, @lt, @m, @q, @ed, @ve, @dr, @vd, @rs, @po, @inv, @con, @pd, @sd, @end, @me, @ar, @bi, @uc, @tc, @cu, @nd, @dep, @ow, @st, @no";

    private static void BindLicense(NpgsqlCommand cmd, LicenseBody b)
    {
        cmd.Parameters.AddWithValue("n", b.Name!.Trim());
        cmd.Parameters.AddWithValue("lt", b.LicenseType ?? "perpetual");
        cmd.Parameters.Add(T("m", b.Metric));
        cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Integer) { Value = b.Unlimited == true ? DBNull.Value : (object?)b.Quantity ?? DBNull.Value });
        cmd.Parameters.Add(T("ed", Trim(b.Edition, 100)));
        cmd.Parameters.Add(T("ve", Trim(b.Version, 100)));
        cmd.Parameters.AddWithValue("dr", b.DowngradeRights ?? false);
        cmd.Parameters.Add(T("vd", Trim(b.Vendor, 200)));
        cmd.Parameters.Add(T("rs", Trim(b.Reseller, 200)));
        cmd.Parameters.Add(T("po", Trim(b.PoNumber, 100)));
        cmd.Parameters.Add(T("inv", Trim(b.InvoiceNumber, 100)));
        cmd.Parameters.Add(T("con", Trim(b.ContractNumber, 100)));
        cmd.Parameters.Add(D("pd", b.PurchaseDate));
        cmd.Parameters.Add(D("sd", b.StartDate));
        cmd.Parameters.Add(D("end", b.EndDate));
        cmd.Parameters.Add(D("me", b.MaintenanceEnd));
        cmd.Parameters.AddWithValue("ar", b.AutoRenew ?? false);
        cmd.Parameters.Add(T("bi", b.Billing));
        cmd.Parameters.Add(new NpgsqlParameter("uc", NpgsqlDbType.Numeric) { Value = (object?)b.UnitCost ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("tc", NpgsqlDbType.Numeric) { Value = (object?)(b.TotalCost ?? (b.UnitCost is { } u && b.Quantity is { } q ? u * q : null)) ?? DBNull.Value });
        cmd.Parameters.AddWithValue("cu", string.IsNullOrWhiteSpace(b.Currency) ? "USD" : b.Currency.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("nd", b.NotifyDays ?? 30);
        cmd.Parameters.Add(new NpgsqlParameter("dep", NpgsqlDbType.Uuid) { Value = (object?)b.DepartmentId ?? DBNull.Value });
        cmd.Parameters.Add(T("ow", Trim(b.Owner, 200)));
        cmd.Parameters.AddWithValue("st", b.Status ?? "active");
        cmd.Parameters.Add(T("no", Trim(b.Notes, 4000)));
    }

    private static IResult? ValidateLicense(LicenseBody b, bool create)
    {
        if (create && b.ProductId is null) return Validation.Problem("productId", "Choose the software product.");
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 200) return Validation.Problem("name", "Enter a name for the licence (up to 200 characters).");
        if (b.LicenseType is not null && !Types.Contains(b.LicenseType)) return Validation.Problem("licenseType", "Choose the licence type.");
        if (b.Metric is not null && !Metrics.Contains(b.Metric)) return Validation.Problem("metric", "Choose how the licence is counted.");
        if (b.Billing is not null && !Billings.Contains(b.Billing)) return Validation.Problem("billing", "Choose the billing period.");
        if (b.Unlimited != true && b.Quantity is null && b.LicenseType is not ("freeware" or "open_source" or "enterprise")) return Validation.Problem("quantity", "Enter how many licences were bought, or choose Unlimited.");
        if (b.Quantity is < 0 or > 10_000_000) return Validation.Problem("quantity", "Enter a quantity between 0 and 10,000,000.");
        if (b.LicenseType is "subscription" or "trial" && b.EndDate is null) return Validation.Problem("endDate", "A subscription needs its end or renewal date.");
        if (b.StartDate is { } s && b.EndDate is { } e && e < s) return Validation.Problem("endDate", "The end date is before the start date.");
        if (b.NotifyDays is < 0 or > 365) return Validation.Problem("notifyDays", "Choose 0 to 365 days.");
        if (b.UnitCost is < 0 || b.TotalCost is < 0) return Validation.Problem("unitCost", "Costs cannot be negative.");
        if (b.Currency is { Length: > 0 } c && (c.Trim().Length != 3 || !c.Trim().All(char.IsLetter))) return Validation.Problem("currency", "Use a three-letter currency code, for example USD, EUR or INR.");
        if (b.Status is not (null or "active" or "retired")) return Validation.Problem("status", "Choose active or retired.");
        if (b.LicenseKey is { Length: > 2000 }) return Validation.Problem("licenseKey", "The licence key is too long.");
        return null;
    }

    private static object Redacted(LicenseBody b) => b with { LicenseKey = string.IsNullOrEmpty(b.LicenseKey) ? null : "(set)" };

    /// <summary>The last five characters of a licence key (what the console shows).</summary>
    public static string KeyHint(string key)
    {
        var k = new string(key.Where(char.IsLetterOrDigit).ToArray());
        return k.Length <= 5 ? "•••••" : "•••••-" + k[^5..].ToUpperInvariant();
    }

    private static IResult? ValidateProduct(ProductBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 160) return Validation.Problem("name", "Enter the product name (up to 160 characters).");
        if (b.Metric is not null && !Metrics.Contains(b.Metric)) return Validation.Problem("metric", "Choose how the product is counted.");
        var kind = b.Kind ?? "installed";
        if (kind is not ("installed" or "cloud")) return Validation.Problem("kind", "Choose installed software or a cloud subscription.");
        if (kind == "installed" && Clean(b.NamePatterns).Length == 0) return Validation.Problem("namePatterns", "Enter at least one name the software installs under (wildcards * and ? are allowed).");
        if (Clean(b.NamePatterns).Length > 30 || Clean(b.ExcludePatterns).Length > 30 || Clean(b.PublisherPatterns).Length > 30) return Validation.Problem("namePatterns", "Use at most 30 patterns.");
        if (Clean(b.NamePatterns).Any(p => p.Trim('*', '?').Length < 2)) return Validation.Problem("namePatterns", "A pattern needs at least two letters besides wildcards.");
        if (Clean(b.ExePatterns).Length > 30) return Validation.Problem("exePatterns", "Use at most 30 programs.");
        if (Clean(b.ExePatterns).Any(p => p.Contains('/') || p.Contains('\\') || p.Trim('*', '?').Length < 2)) return Validation.Problem("exePatterns", "Enter program file names such as winword.exe (wildcards * and ? are allowed), without folders.");
        return null;
    }

    private static void BindProduct(NpgsqlCommand cmd, ProductBody b)
    {
        cmd.Parameters.AddWithValue("n", b.Name!.Trim());
        cmd.Parameters.Add(T("p", Trim(b.Publisher, 200)));
        cmd.Parameters.AddWithValue("c", string.IsNullOrWhiteSpace(b.Category) ? "Other" : b.Category.Trim());
        cmd.Parameters.AddWithValue("np", Clean(b.NamePatterns));
        cmd.Parameters.AddWithValue("xp", Clean(b.ExcludePatterns));
        cmd.Parameters.AddWithValue("pp", Clean(b.PublisherPatterns));
        cmd.Parameters.AddWithValue("m", b.Metric ?? "device");
        cmd.Parameters.AddWithValue("k", b.Kind ?? "installed");
        cmd.Parameters.AddWithValue("nl", b.NeedsLicense ?? true);
        cmd.Parameters.Add(new NpgsqlParameter("w", NpgsqlDbType.Uuid) { Value = (object?)b.WebAppId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("cs", Clean(b.CloudSkus));
        cmd.Parameters.Add(T("no", Trim(b.Notes, 4000)));
        cmd.Parameters.AddWithValue("ep", Clean(b.ExePatterns).Select(e => e.ToLowerInvariant()).Distinct().ToArray());
    }

    private static IResult? ValidateConnection(ConnectionBody b, bool create)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 120) return Validation.Problem("name", "Enter a name for the connection.");
        if (!Guid.TryParse(b.TenantId?.Trim(), out _) && !(b.TenantId?.Trim().Contains('.') ?? false)) return Validation.Problem("tenantId", "Enter the tenant ID (a GUID) or the primary domain, for example contoso.onmicrosoft.com.");
        if (!Guid.TryParse(b.ClientId?.Trim(), out _)) return Validation.Problem("clientId", "Enter the application (client) ID of the app registration.");
        if (create && string.IsNullOrWhiteSpace(b.ClientSecret)) return Validation.Problem("clientSecret", "Enter the client secret value.");
        return null;
    }

    private static void StartSync(M365Sync sync, Guid id) =>
        _ = Task.Run(async () =>
        {
            try { await sync.RunAsync(id, CancellationToken.None); } catch (Exception) { /* recorded on the connection */ }
        });

    public static async Task<string> AlertsJsonAsync(NpgsqlDataSource ds, string scope, string? status, string? kind, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."resolvedAt" NULLS FIRST, array_position(ARRAY['critical','high','medium','low','info'], t."severity"), t."firstAt" DESC), '[]')::text FROM (
              SELECT s.id AS "id", s.kind AS "kind", s.severity AS "severity", s.title AS "title", s.detail AS "detail", s.link AS "link",
                     s.product_id AS "productId", s.license_id AS "licenseId", s.asset_id AS "assetId", s.app_id AS "appId",
                     s.first_at AS "firstAt", s.last_at AS "lastAt", s.resolved_at AS "resolvedAt", s.notified_at AS "notifiedAt", s.dismissed AS "dismissed", s.dismissed_by AS "dismissedBy"
                FROM zentriq.sam_alerts s
               WHERE (CASE WHEN @scope = 'webapps' THEN s.kind LIKE 'webapp_%' ELSE s.kind NOT LIKE 'webapp_%' END)
                 AND (@k::text IS NULL OR s.kind = @k)
                 AND (CASE COALESCE(@st, 'open') WHEN 'open' THEN s.resolved_at IS NULL AND NOT s.dismissed
                                                 WHEN 'dismissed' THEN s.resolved_at IS NULL AND s.dismissed
                                                 WHEN 'resolved' THEN s.resolved_at IS NOT NULL ELSE true END)
               LIMIT 3000) t
            """);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.Add(T("k", kind));
        cmd.Parameters.Add(T("st", status));
        return await cmd.ExecuteScalarAsync(ct) as string ?? "[]";
    }

    public static async Task<IResult> DismissAsync(NpgsqlDataSource ds, RequestContext rc, long id, bool dismiss, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("UPDATE zentriq.sam_alerts SET dismissed = @d, dismissed_by = CASE WHEN @d THEN @by END WHERE id = @id RETURNING title");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("d", dismiss);
        cmd.Parameters.Add(T("by", rc.UserName));
        var title = await cmd.ExecuteScalarAsync(ct) as string;
        if (title is null) return Results.NotFound();
        await rc.AuditAsync(dismiss ? "licenses.alert_dismiss" : "licenses.alert_restore", title);
        return Results.NoContent();
    }

    public static async Task<string?> ScalarAsync(NpgsqlConnection conn, string sql, Action<NpgsqlCommand>? bind, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 120 };
        bind?.Invoke(cmd);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public static string[] Clean(string[]? values) =>
        (values ?? []).Select(v => v?.Trim() ?? "").Where(v => v.Length is > 0 and <= 200).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();
    public static NpgsqlParameter T(string name, string? value) => new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
    private static NpgsqlParameter D(string name, DateOnly? value) => new(name, NpgsqlDbType.Date) { Value = (object?)value ?? DBNull.Value };
}
