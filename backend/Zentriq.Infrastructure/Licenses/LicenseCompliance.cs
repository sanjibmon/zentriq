using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Licenses;

/// <summary>Settings of Software License Management and Web Apps &amp; Accounts (system_settings['licenses.settings']).</summary>
public sealed class LicenseSettings
{
    [JsonPropertyName("notifyEmails")] public List<string> NotifyEmails { get; set; } = [];
    [JsonPropertyName("notifyInApp")] public bool NotifyInApp { get; set; } = true;
    /// <summary>Default warning before a subscription or term ends (a licence can set its own).</summary>
    [JsonPropertyName("expiryDays")] public int ExpiryDays { get; set; } = 30;
    /// <summary>Licences unused above this share count as over-licensed (possible savings).</summary>
    [JsonPropertyName("unusedPercent")] public int UnusedPercent { get; set; } = 25;
    /// <summary>Web app accounts without a sign-in or use for this many days are inactive.</summary>
    [JsonPropertyName("inactiveDays")] public int InactiveDays { get; set; } = 90;
    /// <summary>Find web applications in use from the devices' DNS cache (Windows agent and WinRM inventory).</summary>
    [JsonPropertyName("webDiscovery")] public bool WebDiscovery { get; set; } = true;

    public const string Key = "licenses.settings";

    public static async Task<LicenseSettings> LoadAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = @k");
        cmd.Parameters.AddWithValue("k", Key);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrEmpty(json) ? new LicenseSettings() : JsonSerializer.Deserialize<LicenseSettings>(json) ?? new LicenseSettings();
    }

    public async Task SaveAsync(NpgsqlDataSource ds, Guid? userId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value, updated_at, updated_by) VALUES (@k, @v, now(), @u)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now(), updated_by = EXCLUDED.updated_by
            """);
        cmd.Parameters.AddWithValue("k", Key);
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(this) });
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Licence position of one software product: what is used against what is owned.</summary>
public sealed record ComplianceRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? Publisher { get; init; }
    public string Category { get; init; } = "Other";
    public string Metric { get; init; } = "device";
    public string Kind { get; init; } = "installed";
    public bool NeedsLicense { get; init; }
    public Guid? WebAppId { get; init; }
    public string[] CloudSkus { get; init; } = [];
    public long Devices { get; init; }
    public long Users { get; init; }
    public long Cores { get; init; }
    public long? CloudConsumed { get; init; }
    public long? CloudUnits { get; init; }
    public long? Accounts { get; init; }
    public long Licenses { get; init; }
    public long ValidLicenses { get; init; }
    public bool Unlimited { get; init; }
    public long Entitled { get; init; }
    public DateOnly? NextEnd { get; init; }
    public DateTimeOffset? CloudNextLifecycle { get; init; }
    public decimal? Cost { get; init; }
    public decimal? UnitCost { get; init; }
    public long Expiring { get; init; }
    public long Allocations { get; init; }
    /// <summary>What the metric counts (devices, users, cores, assigned cloud licences); null = not measured (concurrent use).</summary>
    public long? Consumed { get; init; }
    /// <summary>compliant | shortfall | unlicensed | over_licensed | not_measured | not_required | unused</summary>
    public string Status { get; init; } = "compliant";
    public long Shortfall { get; init; }
    public long Unused { get; init; }
    public decimal? ShortfallCost { get; init; }
    public decimal? UnusedValue { get; init; }
}

public static class LicenseCompliance
{
    /// <summary>
    /// Installations of each product: software inventory rows whose name matches one of its patterns (wildcards), not an
    /// exclude pattern, and — when it has publisher patterns — from that publisher, on active devices.
    /// </summary>
    public const string InstallsCte = """
        inst AS (
            SELECT p.id AS product_id, s.asset_id, max(s.version) AS version, min(s.name) AS software
              FROM p JOIN zentriq.software_inventory s ON s.name ILIKE ANY (zentriq.sam_like(p.name_patterns))
              JOIN zentriq.assets a ON a.id = s.asset_id AND a.status = 'active'
             WHERE p.kind = 'installed' AND cardinality(p.name_patterns) > 0
               AND NOT (s.name ILIKE ANY (zentriq.sam_like(p.exclude_patterns)))
               AND (cardinality(p.publisher_patterns) = 0 OR s.publisher ILIKE ANY (zentriq.sam_like(p.publisher_patterns)))
             GROUP BY 1, 2)
        """;

    private const string Sql = $$"""
        WITH p AS (SELECT * FROM zentriq.sam_products WHERE @pid::uuid IS NULL OR id = @pid),
        {{InstallsCte}},
        used AS (
            SELECT i.product_id, count(*) AS devices,
                   count(DISTINCT COALESCE(lower(NULLIF(d.document #>> '{users,loggedOn}', '')), i.asset_id::text)) AS users,
                   COALESCE(sum(h.cpu_cores), 0) AS cores
              FROM inst i LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = i.asset_id LEFT JOIN zentriq.hardware_inventory h ON h.asset_id = i.asset_id
             GROUP BY 1),
        cloud AS (
            SELECT p.id AS product_id, sum(k.consumed) AS consumed, sum(k.enabled_units + k.warning) AS units, min(k.next_lifecycle) AS next_lifecycle
              FROM p JOIN zentriq.sam_cloud_skus k ON k.sku_part = ANY (p.cloud_skus) GROUP BY 1),
        accounts AS (
            SELECT p.id AS product_id, count(*) AS n FROM p JOIN zentriq.webapp_accounts w ON w.app_id = p.webapp_id AND w.status IN ('active','invited') GROUP BY 1),
        lic AS (
            SELECT l.product_id,
                   count(*) FILTER (WHERE l.status = 'active') AS licenses,
                   count(*) FILTER (WHERE l.status = 'active' AND (l.end_date IS NULL OR l.end_date >= current_date)) AS valid,
                   COALESCE(bool_or(l.status = 'active' AND (l.end_date IS NULL OR l.end_date >= current_date)
                        AND (l.license_type IN ('freeware','open_source') OR COALESCE(l.metric, p.metric) = 'site' OR (l.quantity IS NULL AND l.cloud_sku IS NULL))), false) AS unlimited,
                   COALESCE(sum(CASE WHEN l.status = 'active' AND (l.end_date IS NULL OR l.end_date >= current_date)
                        THEN COALESCE(l.quantity, k.enabled_units + k.warning, 0) END), 0) AS entitled,
                   min(l.end_date) FILTER (WHERE l.status = 'active' AND l.end_date >= current_date) AS next_end,
                   sum(l.total_cost) FILTER (WHERE l.status = 'active') AS cost,
                   avg(l.unit_cost) FILTER (WHERE l.status = 'active' AND l.unit_cost IS NOT NULL) AS unit_cost,
                   count(*) FILTER (WHERE l.status = 'active' AND l.end_date BETWEEN current_date AND current_date + l.notify_days) AS expiring
              FROM zentriq.sam_licenses l JOIN p ON p.id = l.product_id
              LEFT JOIN zentriq.sam_cloud_skus k ON k.connection_id = l.cloud_connection_id AND k.sku_part = l.cloud_sku
             GROUP BY 1),
        alloc AS (SELECT l.product_id, count(*) AS n FROM zentriq.sam_allocations x JOIN zentriq.sam_licenses l ON l.id = x.license_id JOIN p ON p.id = l.product_id GROUP BY 1)
        SELECT p.id, p.name, p.publisher, p.category, p.metric, p.kind, p.needs_license, p.webapp_id, p.cloud_skus,
               COALESCE(u.devices, 0), COALESCE(u.users, 0), COALESCE(u.cores, 0), c.consumed, c.units, a.n,
               COALESCE(l.licenses, 0), COALESCE(l.valid, 0), COALESCE(l.unlimited, false), COALESCE(l.entitled, 0), l.next_end, c.next_lifecycle,
               l.cost, l.unit_cost, COALESCE(l.expiring, 0), COALESCE(al.n, 0)
          FROM p
          LEFT JOIN used u ON u.product_id = p.id LEFT JOIN cloud c ON c.product_id = p.id LEFT JOIN accounts a ON a.product_id = p.id
          LEFT JOIN lic l ON l.product_id = p.id LEFT JOIN alloc al ON al.product_id = p.id
         ORDER BY lower(p.name)
        """;

    public static async Task<List<ComplianceRow>> LoadAsync(NpgsqlConnection conn, Guid? productId, int unusedPercent, CancellationToken ct)
    {
        var list = new List<ComplianceRow>();
        await using var cmd = new NpgsqlCommand(Sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.Add(new NpgsqlParameter("pid", NpgsqlDbType.Uuid) { Value = (object?)productId ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            long? NL(int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));
            var row = new ComplianceRow
            {
                Id = r.GetGuid(0), Name = r.GetString(1), Publisher = r.IsDBNull(2) ? null : r.GetString(2), Category = r.GetString(3),
                Metric = r.GetString(4), Kind = r.GetString(5), NeedsLicense = r.GetBoolean(6), WebAppId = r.IsDBNull(7) ? null : r.GetGuid(7),
                CloudSkus = r.GetFieldValue<string[]>(8),
                Devices = Convert.ToInt64(r.GetValue(9)), Users = Convert.ToInt64(r.GetValue(10)), Cores = Convert.ToInt64(r.GetValue(11)),
                CloudConsumed = NL(12), CloudUnits = NL(13), Accounts = NL(14),
                Licenses = Convert.ToInt64(r.GetValue(15)), ValidLicenses = Convert.ToInt64(r.GetValue(16)), Unlimited = r.GetBoolean(17),
                Entitled = Convert.ToInt64(r.GetValue(18)),
                NextEnd = r.IsDBNull(19) ? null : DateOnly.FromDateTime(r.GetDateTime(19)),
                CloudNextLifecycle = r.IsDBNull(20) ? null : r.GetFieldValue<DateTimeOffset>(20),
                Cost = r.IsDBNull(21) ? null : r.GetDecimal(21), UnitCost = r.IsDBNull(22) ? null : Math.Round(r.GetDecimal(22), 2),
                Expiring = Convert.ToInt64(r.GetValue(23)), Allocations = Convert.ToInt64(r.GetValue(24)),
            };
            list.Add(Evaluate(row, unusedPercent));
        }
        return list;
    }

    /// <summary>Consumption under the product's metric and the resulting licence position.</summary>
    public static ComplianceRow Evaluate(ComplianceRow p, int unusedPercent)
    {
        long? consumed = p.Kind == "cloud"
            ? p.CloudConsumed ?? p.Accounts ?? 0
            : p.Metric switch { "user" => p.Users, "core" => p.Cores, "concurrent" => null, _ => p.Devices };
        // A cloud product without licences recorded here: what Microsoft 365 says was bought.
        var entitled = p.Kind == "cloud" && p.ValidLicenses == 0 && p.CloudUnits is { } units ? units : p.Entitled;
        var hasLicences = p.ValidLicenses > 0 || (p.Kind == "cloud" && p.CloudUnits is > 0);

        string status; long shortfall = 0, unused = 0;
        if (!p.NeedsLicense) status = "not_required";
        else if (!hasLicences) status = (consumed ?? p.Devices) > 0 ? "unlicensed" : "unused";
        else if (p.Unlimited) status = "compliant";
        else if (consumed is null) status = "not_measured";
        else if (consumed > entitled) { status = "shortfall"; shortfall = consumed.Value - entitled; }
        else
        {
            unused = entitled - consumed.Value;
            status = entitled > 0 && unused >= 2 && unused * 100 >= entitled * Math.Clamp(unusedPercent, 1, 100) ? "over_licensed" : "compliant";
        }
        return p with
        {
            Consumed = consumed, Entitled = entitled, Status = status, Shortfall = shortfall, Unused = unused,
            ShortfallCost = shortfall > 0 && p.UnitCost is { } c1 ? c1 * shortfall : null,
            UnusedValue = unused > 0 && p.UnitCost is { } c2 ? c2 * unused : null,
        };
    }

    /// <summary>Wildcard match as the database does it (case-insensitive, * and ?), for tests and previews.</summary>
    public static bool Like(string value, string pattern)
    {
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
    }
}
