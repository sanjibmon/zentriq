using Npgsql;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Lifecycle.Warranty;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Online warranty lookup (Asset Lifecycle): vendor API keys (Dell, Lenovo, HP), lookups per device or for all devices,
/// the answer per device and the warranty expiry overview for the dashboard.
/// </summary>
public static class WarrantyEndpoints
{
    public sealed record ProviderBody(bool? Enabled, string? ClientId, string? Secret, string? ApiUrl);
    public sealed record TestBody(string? Serial);
    public sealed record LookupBody(Guid[]? AssetIds, bool? All);

    private const string VendorCase = """
        CASE WHEN v.manufacturer ILIKE '%dell%' THEN 'dell' WHEN v.manufacturer ILIKE '%lenovo%' THEN 'lenovo'
             WHEN (v.manufacturer ILIKE 'hp%' OR v.manufacturer ILIKE '%hewlett%') AND v.manufacturer NOT ILIKE '%enterprise%' THEN 'hp' END
        """;

    public static void MapWarrantyEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/lifecycle/warranty").WithTags("Warranty lookup").RequireModule(Modules.Lifecycle).RequireAuthorization(Permissions.LifecycleView);

        g.MapGet("", async (WarrantyService w, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var vendors = new List<object>();
            await using (var cmd = ds.CreateCommand($"""
                WITH c AS (SELECT v.asset_id, {VendorCase} AS vendor FROM zentriq.v_asset_lifecycle v WHERE v.stage <> 'disposed' AND v.serial IS NOT NULL)
                SELECT c.vendor, count(*), count(*) FILTER (WHERE w.status = 'found'), count(*) FILTER (WHERE w.status = 'not_found'),
                       count(*) FILTER (WHERE w.status = 'error'), count(*) FILTER (WHERE w.status = 'no_key'), count(*) FILTER (WHERE w.asset_id IS NULL), max(w.checked_at)
                  FROM c LEFT JOIN zentriq.warranty_lookups w ON w.asset_id = c.asset_id
                 WHERE c.vendor IS NOT NULL GROUP BY c.vendor ORDER BY c.vendor
                """))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    vendors.Add(new
                    {
                        vendor = r.GetString(0), devices = r.GetInt64(1), found = r.GetInt64(2), notFound = r.GetInt64(3), errors = r.GetInt64(4), noKey = r.GetInt64(5),
                        notChecked = r.GetInt64(6), lastCheckedAt = r.IsDBNull(7) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(7),
                    });
            bool requested;
            await using (var q = ds.CreateCommand($"SELECT EXISTS (SELECT 1 FROM zentriq.system_settings WHERE key = '{WarrantyService.RequestedKey}')"))
                requested = await q.ExecuteScalarAsync(ct) is true;
            return Results.Ok(new { settings = await w.SettingsAsync(ct), providers = await w.ProvidersAsync(ct), vendors, requested });
        });

        g.MapPut("/settings", async (WarrantySettings body, WarrantyService w, RequestContext rc, CancellationToken ct) =>
        {
            if (body.RefreshDays is < 7 or > 365) return Validation.Problem("refreshDays", "Between 7 and 365 days.");
            await w.SaveSettingsAsync(body, rc.UserName, ct);
            await rc.AuditAsync("warranty.settings", null, detail: body);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapPut("/providers/{vendor}", async (string vendor, ProviderBody body, WarrantyService w, RequestContext rc, CancellationToken ct) =>
        {
            vendor = vendor.ToLowerInvariant();
            if (!WarrantyClients.Vendors.Contains(vendor)) return Results.NotFound();
            var clientId = body.ClientId?.Trim();
            if (clientId is { Length: > 200 }) return Validation.Problem("clientId", "The client ID is too long.");
            if (body.Secret is { Length: > 500 }) return Validation.Problem("secret", "The secret is too long.");
            if (!string.IsNullOrWhiteSpace(body.ApiUrl) && (!Uri.TryCreate(body.ApiUrl.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
                return Validation.Problem("apiUrl", "Enter an https:// address, or leave it empty for the vendor's standard address.");
            if (!string.IsNullOrEmpty(clientId) && vendor != "lenovo" && string.IsNullOrEmpty(body.Secret))
            {
                var existing = (await w.ProvidersAsync(ct)).First(p => p.Vendor == vendor);
                if (!existing.Configured) return Validation.Problem("secret", "Enter the client secret.");
            }
            await w.SaveProviderAsync(vendor, body.Enabled ?? true, clientId, body.Secret, body.ApiUrl, rc.UserName, ct);
            await rc.AuditAsync("warranty.provider", WarrantyClients.Label(vendor), detail: new { vendor, enabled = body.Enabled ?? true, configured = !string.IsNullOrEmpty(clientId), secretChanged = !string.IsNullOrEmpty(body.Secret), body.ApiUrl });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.SettingsManage);

        g.MapPost("/providers/{vendor}/test", async (string vendor, TestBody body, WarrantyService w, RequestContext rc, CancellationToken ct) =>
        {
            vendor = vendor.ToLowerInvariant();
            if (!WarrantyClients.Vendors.Contains(vendor)) return Results.NotFound();
            var (ok, message) = await w.TestAsync(vendor, body.Serial, ct);
            await rc.AuditAsync("warranty.test", WarrantyClients.Label(vendor), detail: new { ok, body.Serial });
            return Results.Ok(new { ok, message });
        }).RequireAuthorization(Permissions.SettingsManage);

        // Look up now: the selected devices (up to 50 at once, answered right away) or every Dell, Lenovo and HP device
        // (the worker starts within a minute).
        g.MapPost("/lookup", async (LookupBody body, WarrantyService w, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var ids = body.AssetIds?.Distinct().ToArray() ?? [];
            if (body.All == true || ids.Length > 50)
            {
                await using (var cmd = ds.CreateCommand($"""
                    INSERT INTO zentriq.system_settings (key, value, updated_at) VALUES ('{WarrantyService.RequestedKey}', to_jsonb(now()), now())
                    ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
                    """))
                    await cmd.ExecuteNonQueryAsync(ct);
                if (ids.Length > 0)
                {
                    await using var due = ds.CreateCommand("DELETE FROM zentriq.warranty_lookups WHERE asset_id = ANY(@ids)");
                    due.Parameters.AddWithValue("ids", ids);
                    await due.ExecuteNonQueryAsync(ct);
                }
                await rc.AuditAsync("warranty.lookup", "all", detail: new { devices = ids.Length });
                return Results.Ok(new { queued = true, message = "Warranty lookup started. Devices are checked in the background within a few minutes." });
            }
            if (ids.Length == 0) return Validation.Problem("assetIds", "Select devices.");
            var r = await w.LookupAsync(ids, rc.UserName, ct);
            await rc.AuditAsync("warranty.lookup", $"{ids.Length} device(s)", detail: new { r.Checked, r.Found, r.NotFound, r.Errors, r.NoKey, r.Unsupported });
            var parts = new List<string>();
            if (r.Found > 0) parts.Add($"{r.Found} warranty(ies) found");
            if (r.NotFound > 0) parts.Add($"{r.NotFound} not known to the manufacturer");
            if (r.Unsupported > 0) parts.Add($"{r.Unsupported} not Dell, Lenovo or HP or without a serial number");
            var message = (parts.Count > 0 ? string.Join(", ", parts) + ". " : "") + string.Join(" ", r.Messages);
            return Results.Ok(new { queued = false, r.Checked, r.Found, r.NotFound, r.Errors, r.NoKey, r.Unsupported, message = message.Trim() });
        }).RequireAuthorization(Permissions.LifecycleManage);

        g.MapGet("/assets/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT json_build_object('vendor', w.vendor, 'serial', w.serial, 'status', w.status, 'checkedAt', w.checked_at, 'product', w.product,
                       'shipDate', w.ship_date, 'warrantyStart', w.warranty_start, 'warrantyEnd', w.warranty_end, 'serviceLevel', w.service_level,
                       'entitlements', w.entitlements, 'message', w.message, 'applied', w.applied)::text
                  FROM zentriq.warranty_lookups w WHERE w.asset_id = @a
                """);
            cmd.Parameters.AddWithValue("a", id);
            return await cmd.ExecuteScalarAsync(ct) is string json ? Results.Text(json, "application/json") : Results.Ok(new { status = (string?)null });
        });

        // Warranty expiry of IT assets in use or in stock: buckets, the next to end, and the dashboard highlight counts.
        g.MapGet("/expiry", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT json_build_object(
                  'expired', count(*) FILTER (WHERE warranty_days < 0),
                  'd30', count(*) FILTER (WHERE warranty_days BETWEEN 0 AND 30),
                  'd90', count(*) FILTER (WHERE warranty_days BETWEEN 31 AND 90),
                  'd365', count(*) FILTER (WHERE warranty_days BETWEEN 91 AND 365),
                  'later', count(*) FILTER (WHERE warranty_days > 365),
                  'unknown', count(*) FILTER (WHERE warranty_end IS NULL),
                  'total', count(*),
                  'next', COALESCE((SELECT json_agg(x ORDER BY x."warrantyEnd") FROM (
                      SELECT v2.asset_id AS "id", COALESCE(v2.hostname, v2.serial, v2.model) AS "name", v2.warranty_end AS "warrantyEnd", v2.warranty_days AS "days",
                             v2.warranty_provider AS "provider", v2.assigned_to AS "assignedTo"
                        FROM zentriq.v_asset_lifecycle v2 WHERE v2.stage IN ('in_use','in_stock') AND v2.warranty_days BETWEEN 0 AND 90
                       ORDER BY v2.warranty_end LIMIT 8) x), '[]'::json))::text
                  FROM zentriq.v_asset_lifecycle WHERE stage IN ('in_use','in_stock')
                """);
            return Results.Text((string)(await cmd.ExecuteScalarAsync(ct))!, "application/json");
        });
    }
}
