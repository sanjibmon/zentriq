using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace Zentriq.Infrastructure.WebApps;

/// <summary>Well-known web (SaaS) applications with the host names they are used under. Ships with Zentriq.</summary>
public sealed class WebAppCatalog
{
    public sealed class Entry
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("vendor")] public string? Vendor { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; } = "Other";
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("domains")] public List<string> Domains { get; set; } = [];
    }

    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("apps")] public List<Entry> Apps { get; set; } = [];

    private static readonly Lazy<WebAppCatalog> Shared = new(Load);
    public static WebAppCatalog Standard => Shared.Value;

    private static WebAppCatalog Load()
    {
        using var s = typeof(WebAppCatalog).Assembly.GetManifestResourceStream("Zentriq.WebApps.Catalog.json")
                      ?? throw new InvalidOperationException("The web application catalogue is missing from the build.");
        return JsonSerializer.Deserialize<WebAppCatalog>(s) ?? new WebAppCatalog();
    }

    public Entry? Find(string key) => Apps.FirstOrDefault(a => a.Key == key);

    /// <summary>A host name belongs to a domain when it is the domain or a name under it (teams.microsoft.com, x.slack.com).</summary>
    public static bool HostMatches(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    /// <summary>The domain that matches a host best (the longest), or null.</summary>
    public static string? BestDomain(string host, IEnumerable<string> domains) =>
        domains.Where(d => d.Length > 0 && HostMatches(host, d)).OrderByDescending(d => d.Length).FirstOrDefault();

    /// <summary>Normalises a domain typed in the console ("https://app.example.com/x" → "app.example.com").</summary>
    public static string? CleanDomain(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var d = s.Trim().ToLowerInvariant();
        var i = d.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) d = d[(i + 3)..];
        d = d.Split('/', '?', '#')[0].Split(':')[0].TrimStart('*', '.').TrimEnd('.');
        if (d.Length is < 3 or > 253 || !d.Contains('.') || d.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))) return null;
        return d;
    }
}

/// <summary>Records web applications used on a device (from the host names in its inventory).</summary>
public static class WebAppUsage
{
    private sealed record App(Guid? Id, string? CatalogKey, string Name, string[] Domains);

    /// <summary>
    /// Matches the host names against the organisation's web apps and the catalogue; a catalogue app seen for the first
    /// time is added (status "review"). Only matches are stored — the other host names are discarded.
    /// Returns the number of applications seen.
    /// </summary>
    public static async Task<int> RecordAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid assetId, IReadOnlyCollection<string>? hosts, string? user, CancellationToken ct)
    {
        if (hosts is null || hosts.Count == 0) return 0;
        await using (var on = new NpgsqlCommand("SELECT COALESCE((value->>'webDiscovery')::boolean, true) FROM zentriq.system_settings WHERE key = 'licenses.settings'", conn, tx))
            if (await on.ExecuteScalarAsync(ct) is false) return 0;

        var apps = new List<App>();
        await using (var cmd = new NpgsqlCommand("SELECT id, catalog_key, name, domains FROM zentriq.webapps WHERE cardinality(domains) > 0", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) apps.Add(new App(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3)));
        var known = apps.Where(a => a.CatalogKey is not null).Select(a => a.CatalogKey!).ToHashSet();
        foreach (var e in WebAppCatalog.Standard.Apps.Where(e => !known.Contains(e.Key) && e.Domains.Count > 0))
            apps.Add(new App(null, e.Key, e.Name, e.Domains.ToArray()));

        // Per host the most specific domain wins (teams.microsoft.com is Teams, not Microsoft 365).
        var seen = new HashSet<App>();
        foreach (var h in hosts.Take(2000))
        {
            App? best = null; var len = 0;
            foreach (var a in apps)
                foreach (var d in a.Domains)
                    if (d.Length > len && WebAppCatalog.HostMatches(h, d)) { best = a; len = d.Length; }
            if (best is not null) seen.Add(best);
        }
        if (seen.Count == 0) return 0;

        var userName = string.IsNullOrWhiteSpace(user) ? "" : user.Trim();
        foreach (var a in seen)
        {
            var id = a.Id;
            if (id is null)
            {
                var e = WebAppCatalog.Standard.Find(a.CatalogKey!)!;
                await using var ins = new NpgsqlCommand("""
                    INSERT INTO zentriq.webapps (catalog_key, name, vendor, category, url, domains, status, source, first_seen_at)
                    VALUES (@k, @n, @v, @c, @u, @d, 'review', 'usage', now())
                    ON CONFLICT DO NOTHING RETURNING id
                    """, conn, tx);
                ins.Parameters.AddWithValue("k", e.Key);
                ins.Parameters.AddWithValue("n", e.Name);
                ins.Parameters.AddWithValue("v", (object?)e.Vendor ?? DBNull.Value);
                ins.Parameters.AddWithValue("c", e.Category);
                ins.Parameters.AddWithValue("u", (object?)e.Url ?? DBNull.Value);
                ins.Parameters.AddWithValue("d", e.Domains.ToArray());
                id = await ins.ExecuteScalarAsync(ct) as Guid?;
                if (id is null)
                {
                    await using var sel = new NpgsqlCommand("SELECT id FROM zentriq.webapps WHERE catalog_key = @k OR lower(btrim(name)) = lower(@n) LIMIT 1", conn, tx);
                    sel.Parameters.AddWithValue("k", e.Key);
                    sel.Parameters.AddWithValue("n", e.Name);
                    id = await sel.ExecuteScalarAsync(ct) as Guid?;
                }
                if (id is null) continue;
            }
            await using var up = new NpgsqlCommand("""
                INSERT INTO zentriq.webapp_usage (app_id, asset_id, user_name) VALUES (@a, @s, @u)
                ON CONFLICT (app_id, asset_id, user_name) DO UPDATE SET last_seen = now(),
                    days = webapp_usage.days + CASE WHEN webapp_usage.last_day < current_date THEN 1 ELSE 0 END, last_day = current_date;
                UPDATE zentriq.webapps SET first_seen_at = COALESCE(first_seen_at, now()) WHERE id = @a;
                """, conn, tx);
            up.Parameters.AddWithValue("a", id.Value);
            up.Parameters.AddWithValue("s", assetId);
            up.Parameters.AddWithValue("u", userName);
            await up.ExecuteNonQueryAsync(ct);
        }
        return seen.Count;
    }
}
