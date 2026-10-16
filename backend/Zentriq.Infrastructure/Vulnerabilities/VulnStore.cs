using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Vulnerabilities;

public sealed record SlaDays(int Critical, int High, int Medium, int Low)
{
    public static readonly SlaDays Default = new(15, 30, 90, 180);
    public int For(string? severity) => severity switch { "critical" => Critical, "high" => High, "medium" => Medium, _ => Low };
}

public sealed record EvaluationResult(int Products, int Installs, int Open, int New, int Fixed, int Reopened)
{
    /// <summary>Windows devices whose build was compared with Microsoft's fixed builds.</summary>
    public int OsAssessed { get; init; }
    /// <summary>Windows devices that could not be assessed (no UBR in the inventory, or a build Microsoft does not list).</summary>
    public int OsNotAssessed { get; init; }
}

/// <summary>Vulnerability data: CVE catalogue, KEV/EPSS enrichment, software mappings and the findings evaluation.</summary>
public sealed class VulnStore(NpgsqlDataSource ds)
{
    // ---- settings -----------------------------------------------------------------------------------

    public async Task<JsonElement?> SettingAsync(string key, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = @k");
        cmd.Parameters.AddWithValue("k", key);
        var v = await cmd.ExecuteScalarAsync(ct) as string;
        return v is null ? null : JsonDocument.Parse(v).RootElement.Clone();
    }

    public async Task SetSettingAsync(string key, string json, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value) VALUES (@k, @v::jsonb)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("v", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SlaDays> SlaAsync(CancellationToken ct)
    {
        var e = await SettingAsync("vuln.sla_days", ct);
        if (e is not { ValueKind: JsonValueKind.Object } o) return SlaDays.Default;
        int D(string n, int d) => o.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) && i is > 0 and <= 3650 ? i : d;
        return new SlaDays(D("critical", 15), D("high", 30), D("medium", 90), D("low", 180));
    }

    public async Task<decimal> MinCvssAsync(CancellationToken ct)
    {
        var e = await SettingAsync("vuln.min_cvss", ct);
        return e is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var d) ? d : 0m;
    }

    public async Task SetFeedStatusAsync(string source, bool ok, int? items, string? message, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.vuln_feed_status SET last_run_at = now(), last_ok_at = CASE WHEN @ok THEN now() ELSE last_ok_at END,
                   items = COALESCE(@n, items), message = @m
             WHERE source = @s
            """);
        cmd.Parameters.AddWithValue("s", source);
        cmd.Parameters.AddWithValue("ok", ok);
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Integer) { Value = (object?)items ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Text) { Value = (object?)message ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- product mappings ---------------------------------------------------------------------------

    /// <summary>Adds a mapping row for every new installed title (built-in mapping when known, otherwise unmapped).</summary>
    public async Task<int> EnsureMappingsAsync(CancellationToken ct)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = ds.CreateCommand("SELECT software_key FROM zentriq.vuln_product_map"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) existing.Add(r.GetString(0));

        var fresh = new Dictionary<string, (string Name, string? Publisher)>(StringComparer.Ordinal);
        await using (var cmd = ds.CreateCommand("""
            SELECT s.name, min(s.publisher) FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id
             WHERE a.status = 'active' GROUP BY s.name
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var key = ProductCatalog.Key(name);
                if (key.Length > 300 || existing.Contains(key) || fresh.ContainsKey(key)) continue;
                fresh[key] = (name, r.IsDBNull(1) ? null : r.GetString(1));
            }
        if (fresh.Count == 0) return 0;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var (key, (name, publisher)) in fresh)
        {
            var known = ProductCatalog.Lookup(key);
            await using var ins = new NpgsqlCommand("""
                INSERT INTO zentriq.vuln_product_map (software_key, software_name, publisher, vendor, product, status)
                VALUES (@k, @n, @p, @v, @pr, @s) ON CONFLICT (software_key) DO NOTHING
                """, conn, tx);
            ins.Parameters.AddWithValue("k", key);
            ins.Parameters.AddWithValue("n", name);
            ins.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = (object?)publisher ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Text) { Value = (object?)known?.Vendor ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("pr", NpgsqlDbType.Text) { Value = (object?)known?.Product ?? DBNull.Value });
            ins.Parameters.AddWithValue("s", known is null ? "unmapped" : "auto");
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return fresh.Count;
    }

    /// <summary>Distinct mapped vendor:product pairs with the oldest successful NVD sync among their titles.</summary>
    public async Task<List<(string Vendor, string Product, DateTimeOffset? Synced)>> MappedProductsAsync(CancellationToken ct)
    {
        var list = new List<(string, string, DateTimeOffset?)>();
        await using var cmd = ds.CreateCommand("""
            SELECT vendor, product, CASE WHEN bool_or(nvd_synced_at IS NULL) THEN NULL ELSE min(nvd_synced_at) END
              FROM zentriq.vuln_product_map WHERE status IN ('auto','manual') AND vendor IS NOT NULL AND product IS NOT NULL
             GROUP BY vendor, product ORDER BY vendor, product
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2)));
        return list;
    }

    public async Task MarkSyncedAsync(string vendor, string product, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("UPDATE zentriq.vuln_product_map SET nvd_synced_at = @t WHERE vendor = @v AND product = @p");
        cmd.Parameters.AddWithValue("t", at);
        cmd.Parameters.AddWithValue("v", vendor);
        cmd.Parameters.AddWithValue("p", product);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- CVE catalogue ------------------------------------------------------------------------------

    /// <summary>Inserts or updates CVEs and replaces their application (part "a") match rules.</summary>
    public async Task<int> UpsertCvesAsync(IReadOnlyCollection<CveRecord> cves, CancellationToken ct)
    {
        if (cves.Count == 0) return 0;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var c in cves)
        {
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.vuln_cves (cve_id, published_at, modified_at, status, description, cvss_version, cvss_score, cvss_vector, severity, cwes, reference_urls,
                                               kev, kev_added, kev_due, kev_action, updated_at)
                VALUES (@id, @pub, @mod, @st, @d, @cv, @cs, @vec, @sev, @cwe, @refs, @kev, @ka, @kd, @kact, now())
                ON CONFLICT (cve_id) DO UPDATE SET published_at = EXCLUDED.published_at, modified_at = EXCLUDED.modified_at, status = EXCLUDED.status,
                    description = EXCLUDED.description, cvss_version = EXCLUDED.cvss_version, cvss_score = EXCLUDED.cvss_score, cvss_vector = EXCLUDED.cvss_vector,
                    severity = EXCLUDED.severity, cwes = EXCLUDED.cwes, reference_urls = EXCLUDED.reference_urls,
                    kev = vuln_cves.kev OR EXCLUDED.kev, kev_added = COALESCE(EXCLUDED.kev_added, vuln_cves.kev_added),
                    kev_due = COALESCE(EXCLUDED.kev_due, vuln_cves.kev_due), kev_action = COALESCE(EXCLUDED.kev_action, vuln_cves.kev_action), updated_at = now()
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("id", c.Id);
                cmd.Parameters.Add(Ts("pub", c.Published));
                cmd.Parameters.Add(Ts("mod", c.Modified));
                cmd.Parameters.Add(Txt("st", c.Status));
                cmd.Parameters.Add(Txt("d", c.Description));
                cmd.Parameters.Add(Txt("cv", c.CvssVersion));
                cmd.Parameters.Add(new NpgsqlParameter("cs", NpgsqlDbType.Numeric) { Value = (object?)c.CvssScore ?? DBNull.Value });
                cmd.Parameters.Add(Txt("vec", c.CvssVector));
                cmd.Parameters.AddWithValue("sev", c.Severity is "critical" or "high" or "medium" or "low" ? c.Severity : "none");
                cmd.Parameters.Add(new NpgsqlParameter("cwe", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = c.Cwes.ToArray() });
                cmd.Parameters.Add(new NpgsqlParameter("refs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = c.References.ToArray() });
                cmd.Parameters.AddWithValue("kev", c.KevAdded is not null);
                cmd.Parameters.Add(Day("ka", c.KevAdded));
                cmd.Parameters.Add(Day("kd", c.KevDue));
                cmd.Parameters.Add(Txt("kact", c.KevAction));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var del = new NpgsqlCommand("DELETE FROM zentriq.vuln_cpe_rules WHERE cve_id = @id", conn, tx))
            {
                del.Parameters.AddWithValue("id", c.Id);
                await del.ExecuteNonQueryAsync(ct);
            }
            foreach (var rule in c.Rules.Where(r => r.Part == "a").DistinctBy(r => (r.Vendor, r.Product, r.Version, r.StartIncl, r.StartExcl, r.EndIncl, r.EndExcl)).Take(500))
            {
                await using var ins = new NpgsqlCommand("""
                    INSERT INTO zentriq.vuln_cpe_rules (cve_id, part, vendor, product, version, version_start_incl, version_start_excl, version_end_incl, version_end_excl, criteria)
                    VALUES (@id, @part, @v, @p, @ver, @si, @se, @ei, @ee, @c)
                    """, conn, tx);
                ins.Parameters.AddWithValue("id", c.Id);
                ins.Parameters.AddWithValue("part", rule.Part);
                ins.Parameters.AddWithValue("v", rule.Vendor);
                ins.Parameters.AddWithValue("p", rule.Product);
                ins.Parameters.Add(Txt("ver", rule.Version));
                ins.Parameters.Add(Txt("si", rule.StartIncl));
                ins.Parameters.Add(Txt("se", rule.StartExcl));
                ins.Parameters.Add(Txt("ei", rule.EndIncl));
                ins.Parameters.Add(Txt("ee", rule.EndExcl));
                ins.Parameters.AddWithValue("c", rule.Criteria);
                await ins.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
        return cves.Count;
    }

    /// <summary>Applies the full CISA KEV catalogue: listed CVEs are flagged, CVEs no longer listed are cleared.</summary>
    public async Task<int> ApplyKevAsync(IReadOnlyCollection<KevEntry> kev, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.vuln_cves c SET kev = true, kev_added = u.added, kev_due = u.due, kev_ransomware = u.rw, kev_action = u.act, updated_at = now()
              FROM unnest(@ids, @added, @due, @rw, @act) AS u(id, added, due, rw, act)
             WHERE c.cve_id = u.id;
            UPDATE zentriq.vuln_cves SET kev = false, kev_added = NULL, kev_due = NULL, kev_ransomware = NULL, kev_action = NULL
             WHERE kev AND cve_id <> ALL(@ids);
            """);
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = kev.Select(k => k.CveId).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("added", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = kev.Select(k => k.Added).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("due", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = kev.Select(k => k.Due).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("rw", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = kev.Select(k => k.Ransomware).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("act", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = kev.Select(k => k.Action).ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
        await using var count = ds.CreateCommand("SELECT count(*) FROM zentriq.vuln_cves WHERE kev");
        return Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<int> ApplyEpssAsync(IReadOnlyCollection<EpssEntry> scores, CancellationToken ct)
    {
        if (scores.Count == 0) return 0;
        var updated = 0;
        foreach (var chunk in scores.Chunk(5000))
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.vuln_cves c SET epss = u.e, epss_percentile = u.p, epss_date = COALESCE(u.d, current_date)
                  FROM unnest(@ids, @e, @p, @d) AS u(id, e, p, d) WHERE c.cve_id = u.id
                """);
            cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = chunk.Select(s => s.CveId).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = chunk.Select(s => s.Score).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = chunk.Select(s => s.Percentile).ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = chunk.Select(s => s.Date).ToArray() });
            updated += await cmd.ExecuteNonQueryAsync(ct);
        }
        return updated;
    }

    /// <summary>CVEs that affect mapped software and have no EPSS score from today or yesterday.</summary>
    public async Task<List<string>> CvesNeedingEpssAsync(int max, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT c.cve_id FROM zentriq.vuln_cves c
             WHERE (c.epss_date IS NULL OR c.epss_date < current_date - 1)
               AND (EXISTS (SELECT 1 FROM zentriq.vuln_cpe_rules r
                              JOIN zentriq.vuln_product_map m ON m.vendor = r.vendor AND m.product = r.product AND m.status IN ('auto','manual')
                             WHERE r.cve_id = c.cve_id)
                    OR EXISTS (SELECT 1 FROM zentriq.vuln_os_rules o WHERE o.cve_id = c.cve_id))
             LIMIT @n
            """);
        cmd.Parameters.AddWithValue("n", max);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>
    /// Stores one monthly Microsoft document: CVEs are added or enriched (title, Microsoft severity, exploited) without
    /// overwriting NVD data, and the month's Windows fixed builds replace those stored before for that month.
    /// </summary>
    public async Task<(int Cves, int Rules)> UpsertMsrcAsync(MsrcDocument doc, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var c in doc.Cves)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.vuln_cves (cve_id, published_at, status, title, description, cvss_version, cvss_score, cvss_vector, severity, cwes,
                                               reference_urls, ms_severity, ms_exploited, ms_disclosed, updated_at)
                VALUES (@id, @pub, 'Microsoft', @t, @d, @cv, @cs, @vec, @sev, @cwe, @refs, @ms, @ex, @di, now())
                ON CONFLICT (cve_id) DO UPDATE SET
                    title = EXCLUDED.title, ms_severity = EXCLUDED.ms_severity, ms_exploited = EXCLUDED.ms_exploited, ms_disclosed = EXCLUDED.ms_disclosed,
                    published_at = COALESCE(vuln_cves.published_at, EXCLUDED.published_at),
                    description = COALESCE(vuln_cves.description, EXCLUDED.description),
                    cvss_version = COALESCE(vuln_cves.cvss_version, EXCLUDED.cvss_version),
                    cvss_vector = COALESCE(vuln_cves.cvss_vector, EXCLUDED.cvss_vector),
                    severity = CASE WHEN vuln_cves.cvss_score IS NULL THEN EXCLUDED.severity ELSE vuln_cves.severity END,
                    cvss_score = COALESCE(vuln_cves.cvss_score, EXCLUDED.cvss_score),
                    cwes = CASE WHEN cardinality(vuln_cves.cwes) = 0 THEN EXCLUDED.cwes ELSE vuln_cves.cwes END,
                    reference_urls = CASE WHEN cardinality(vuln_cves.reference_urls) = 0 THEN EXCLUDED.reference_urls ELSE vuln_cves.reference_urls END,
                    updated_at = now()
                """, conn, tx);
            cmd.Parameters.AddWithValue("id", c.Id);
            cmd.Parameters.Add(Ts("pub", c.Published));
            cmd.Parameters.Add(Txt("t", c.Title));
            cmd.Parameters.Add(Txt("d", c.Description ?? c.Title));
            cmd.Parameters.Add(Txt("cv", c.CvssScore is null ? null : c.CvssVector?.StartsWith("CVSS:4", StringComparison.Ordinal) == true ? "4.0" : "3.1"));
            cmd.Parameters.Add(new NpgsqlParameter("cs", NpgsqlDbType.Numeric) { Value = (object?)c.CvssScore ?? DBNull.Value });
            cmd.Parameters.Add(Txt("vec", c.CvssVector));
            cmd.Parameters.AddWithValue("sev", VulnScoring.SeverityFor(c.CvssScore, c.MsSeverity));
            cmd.Parameters.Add(new NpgsqlParameter("cwe", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = c.Cwes.ToArray() });
            cmd.Parameters.Add(new NpgsqlParameter("refs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = new[] { $"https://msrc.microsoft.com/update-guide/vulnerability/{c.Id}" } });
            cmd.Parameters.Add(Txt("ms", c.MsSeverity));
            cmd.Parameters.AddWithValue("ex", c.Exploited);
            cmd.Parameters.AddWithValue("di", c.Disclosed);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var del = new NpgsqlCommand("DELETE FROM zentriq.vuln_os_rules WHERE month = @m", conn, tx))
        {
            del.Parameters.AddWithValue("m", doc.Id);
            await del.ExecuteNonQueryAsync(ct);
        }
        var rules = 0;
        await using (var copy = await conn.BeginBinaryImportAsync(
            "COPY zentriq.vuln_os_rules (cve_id, month, product, product_key, fixed_build, kb, kb_url, supersedes, restart) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var c in doc.Cves)
                foreach (var f in c.Fixes)
                {
                    await copy.StartRowAsync(ct);
                    await copy.WriteAsync(c.Id, NpgsqlDbType.Text, ct);
                    await copy.WriteAsync(doc.Id, NpgsqlDbType.Text, ct);
                    await copy.WriteAsync(f.Product, NpgsqlDbType.Text, ct);
                    await copy.WriteAsync(Msrc.Key(f.Product), NpgsqlDbType.Text, ct);
                    await copy.WriteAsync(f.FixedBuild, NpgsqlDbType.Text, ct);
                    if (f.Kb is null) await copy.WriteNullAsync(ct); else await copy.WriteAsync(f.Kb, NpgsqlDbType.Text, ct);
                    if (f.KbUrl is null) await copy.WriteNullAsync(ct); else await copy.WriteAsync(f.KbUrl, NpgsqlDbType.Text, ct);
                    if (f.Supersedes is null) await copy.WriteNullAsync(ct); else await copy.WriteAsync(f.Supersedes.Length > 200 ? f.Supersedes[..200] : f.Supersedes, NpgsqlDbType.Text, ct);
                    if (f.Restart is null) await copy.WriteNullAsync(ct); else await copy.WriteAsync(f.Restart.Value, NpgsqlDbType.Boolean, ct);
                    rules++;
                }
            await copy.CompleteAsync(ct);
        }
        await tx.CommitAsync(ct);
        return (doc.Cves.Count, rules);
    }

    // ---- evaluation ---------------------------------------------------------------------------------

    private sealed record Rule(string CveId, string? Version, string? StartIncl, string? StartExcl, string? EndIncl, string? EndExcl, decimal? Cvss, decimal? Epss, bool Kev, string Severity, DateOnly? KevDue);
    private sealed record OsRule(string CveId, string FixedBuild, string? Kb, decimal? Cvss, decimal? Epss, bool Exploited, string Severity, DateOnly? KevDue);
    private sealed record Match(Guid AssetId, string CveId, string Name, string Version, string Vendor, string Product, string? Fixed, string Priority, int Risk,
                                string Severity, DateOnly? KevDue, string Source = "software", string? Kb = null, string? Installed = null);

    /// <summary>
    /// Matches installed software against the CVE rules of its mapped product, and Windows devices against Microsoft's
    /// fixed builds, then updates findings: new matches open findings (due date from the SLA, or the CISA due date when
    /// earlier); findings no longer matched are closed as fixed (with a history entry); a fixed finding that matches
    /// again is reopened; expired risk acceptances reopen. Findings being worked on keep their status and assignee.
    /// </summary>
    public async Task<EvaluationResult> EvaluateAsync(CancellationToken ct)
    {
        DateTimeOffset started;
        await using (var now = ds.CreateCommand("SELECT now()")) started = new DateTimeOffset(DateTime.SpecifyKind((DateTime)(await now.ExecuteScalarAsync(ct))!, DateTimeKind.Utc));
        var sla = await SlaAsync(ct);
        var minCvss = await MinCvssAsync(ct);
        var weighted = await SettingAsync("vuln.criticality_weight", ct) is not { ValueKind: JsonValueKind.False };

        await using (var expire = ds.CreateCommand("""
            WITH x AS (
                UPDATE zentriq.vuln_findings SET status = 'open', status_changed_at = now(), exception_reason = NULL, exception_until = NULL, exception_by = NULL, exception_at = NULL
                 WHERE status = 'accepted' AND exception_until IS NOT NULL AND exception_until < now() RETURNING id)
            INSERT INTO zentriq.vuln_finding_events (finding_id, kind, text) SELECT id, 'status', 'Risk acceptance expired; reopened.' FROM x
            """))
            await expire.ExecuteNonQueryAsync(ct);

        var factor = new Dictionary<Guid, decimal>();
        if (weighted)
        {
            await using var cmd = ds.CreateCommand("SELECT id, criticality FROM zentriq.assets WHERE status = 'active' AND criticality <> 'Medium'");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) factor[r.GetGuid(0)] = VulnScoring.CriticalityFactor(r.GetString(1));
        }
        decimal F(Guid asset) => factor.TryGetValue(asset, out var f) ? f : 1m;

        var mapping = new Dictionary<string, (string Vendor, string Product)>(StringComparer.Ordinal);
        await using (var cmd = ds.CreateCommand("SELECT software_key, vendor, product FROM zentriq.vuln_product_map WHERE status IN ('auto','manual') AND vendor IS NOT NULL AND product IS NOT NULL"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) mapping[r.GetString(0)] = (r.GetString(1), r.GetString(2));

        var namesByProduct = new Dictionary<(string, string), List<string>>();
        await using (var cmd = ds.CreateCommand("SELECT DISTINCT s.name FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id WHERE a.status = 'active'"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                if (!mapping.TryGetValue(ProductCatalog.Key(name), out var vp)) continue;
                if (!namesByProduct.TryGetValue(vp, out var l)) namesByProduct[vp] = l = [];
                l.Add(name);
            }

        var matches = new Dictionary<(Guid, string, string, string), Match>();
        var installs = 0;
        foreach (var ((vendor, product), names) in namesByProduct)
        {
            var rules = new List<Rule>();
            await using (var cmd = ds.CreateCommand("""
                SELECT r.cve_id, r.version, r.version_start_incl, r.version_start_excl, r.version_end_incl, r.version_end_excl,
                       c.cvss_score, c.epss, c.kev OR c.ms_exploited, COALESCE(c.severity, 'none'), c.kev_due
                  FROM zentriq.vuln_cpe_rules r JOIN zentriq.vuln_cves c ON c.cve_id = r.cve_id
                 WHERE r.vendor = @v AND r.product = @p AND r.part = 'a'
                   AND COALESCE(c.status, '') NOT IN ('Rejected', 'REJECTED')
                   AND (c.cvss_score IS NULL OR c.cvss_score >= @min)
                """))
            {
                cmd.Parameters.AddWithValue("v", vendor);
                cmd.Parameters.AddWithValue("p", product);
                cmd.Parameters.AddWithValue("min", minCvss);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                    rules.Add(new Rule(r.GetString(0), S(1), S(2), S(3), S(4), S(5),
                        r.IsDBNull(6) ? null : r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetDecimal(7), r.GetBoolean(8), r.GetString(9),
                        r.IsDBNull(10) ? null : r.GetFieldValue<DateOnly>(10)));
                }
            }
            if (rules.Count == 0) continue;
            await using (var cmd = ds.CreateCommand("""
                SELECT DISTINCT s.asset_id, s.name, COALESCE(s.version, '') FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id
                 WHERE a.status = 'active' AND s.name = ANY(@n)
                """))
            {
                cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names.ToArray() });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    installs++;
                    var asset = r.GetGuid(0);
                    var name = r.GetString(1);
                    var version = r.GetString(2);
                    foreach (var rule in rules)
                    {
                        if (!VersionComparer.Matches(version, rule.Version, rule.StartIncl, rule.StartExcl, rule.EndIncl, rule.EndExcl)) continue;
                        var key = (asset, rule.CveId, name, version);
                        if (matches.ContainsKey(key)) continue;
                        matches[key] = new Match(asset, rule.CveId, name, version, vendor, product, rule.EndExcl,
                            VulnScoring.Priority(rule.Cvss, rule.Epss, rule.Kev), VulnScoring.Risk(rule.Cvss, rule.Epss, rule.Kev, F(asset)), rule.Severity, rule.KevDue);
                    }
                }
            }
        }

        // ---- Windows: device build vs. Microsoft fixed builds ----------------------------------------------
        var osRules = new Dictionary<string, List<OsRule>>(StringComparer.Ordinal);
        await using (var cmd = ds.CreateCommand("""
            SELECT o.product_key, o.cve_id, o.fixed_build, o.kb, c.cvss_score, c.epss, c.kev OR c.ms_exploited, COALESCE(c.severity, 'none'), c.kev_due
              FROM zentriq.vuln_os_rules o JOIN zentriq.vuln_cves c ON c.cve_id = o.cve_id
             WHERE o.fixed_build IS NOT NULL AND (c.cvss_score IS NULL OR c.cvss_score >= @min)
            """))
        {
            cmd.Parameters.AddWithValue("min", minCvss);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var pk = r.GetString(0);
                if (!osRules.TryGetValue(pk, out var list)) osRules[pk] = list = [];
                list.Add(new OsRule(r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetDecimal(4),
                    r.IsDBNull(5) ? null : r.GetDecimal(5), r.GetBoolean(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateOnly>(8)));
            }
        }
        int osAssessed = 0, osNotAssessed = 0;
        var keepOs = new List<Guid>();      // Windows devices not assessable now: keep their OS findings as they are
        if (osRules.Count > 0)
        {
            await using var cmd = ds.CreateCommand("""
                SELECT a.id, COALESCE(d.document #>> '{os,name}', a.os_name, 'Windows'), d.document #>> '{os,version}', d.document #>> '{os,ubr}',
                       d.document #>> '{os,architecture}', d.document #>> '{os,isServer}', d.document #>> '{os,installationType}'
                  FROM zentriq.assets a JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id
                 WHERE a.status = 'active' AND d.document #>> '{os,family}' = 'windows'
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var asset = r.GetGuid(0);
                var osName = r.GetString(1);
                var version = S(2);
                var ubr = int.TryParse(S(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : (int?)null;
                var product = Msrc.ProductFor(version, S(5) is { } srv ? srv == "true" : null, S(4), S(6), osName);
                var build = Msrc.FullBuild(version, ubr);
                if (product is null || build is null || !osRules.TryGetValue(Msrc.Key(product), out var rules)) { osNotAssessed++; keepOs.Add(asset); continue; }
                osAssessed++;
                var baseBuild = Msrc.BaseBuild(build);
                // Per CVE the highest fixed build of this build line (a re-released fix supersedes the first one).
                foreach (var g in rules.Where(x => Msrc.BaseBuild(x.FixedBuild) == baseBuild).GroupBy(x => x.CveId))
                {
                    var rule = g.Aggregate((a, b) => VersionComparer.Compare(a.FixedBuild, b.FixedBuild) >= 0 ? a : b);
                    if (!Msrc.IsVulnerable(build, rule.FixedBuild)) continue;
                    var key = (asset, rule.CveId, osName, baseBuild);
                    matches[key] = new Match(asset, rule.CveId, osName, baseBuild, "microsoft", product, rule.FixedBuild,
                        VulnScoring.Priority(rule.Cvss, rule.Epss, rule.Exploited), VulnScoring.Risk(rule.Cvss, rule.Epss, rule.Exploited, F(asset)),
                        rule.Severity, rule.KevDue, "os", rule.Kb, build);
                }
            }
        }

        int added = 0, reopened = 0, fixedCount;
        await using (var conn = await ds.OpenConnectionAsync(ct))
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var create = new NpgsqlCommand("""
                CREATE TEMP TABLE vf_new (asset_id uuid, cve_id text, name text, version text, vendor text, product text, fixed text,
                                          priority text, risk int, severity text, kev_due date, source text, kb text, installed text) ON COMMIT DROP
                """, conn, tx))
                await create.ExecuteNonQueryAsync(ct);
            await using (var copy = await conn.BeginBinaryImportAsync("COPY vf_new (asset_id, cve_id, name, version, vendor, product, fixed, priority, risk, severity, kev_due, source, kb, installed) FROM STDIN (FORMAT BINARY)", ct))
            {
                foreach (var m in matches.Values)
                {
                    await copy.StartRowAsync(ct);
                    await copy.WriteAsync(m.AssetId, NpgsqlDbType.Uuid, ct);
                    await copy.WriteAsync(m.CveId, NpgsqlDbType.Text, ct);
                    await copy.WriteAsync(m.Name, NpgsqlDbType.Text, ct);
                    await copy.WriteAsync(m.Version, NpgsqlDbType.Text, ct);
                    await copy.WriteAsync(m.Vendor, NpgsqlDbType.Text, ct);
                    await copy.WriteAsync(m.Product, NpgsqlDbType.Text, ct);
                    if (m.Fixed is null) await copy.WriteNullAsync(ct); else await copy.WriteAsync(m.Fixed, NpgsqlDbType.Text, ct);
                    await copy.WriteAsync(m.Priority, NpgsqlDbType.Text, ct);
                    await copy.WriteAsync(m.Risk, NpgsqlDbType.Integer, ct);
                    await copy.WriteAsync(m.Severity, NpgsqlDbType.Text, ct);
                    if (m.KevDue is null) await copy.WriteNullAsync(ct); else await copy.WriteAsync(m.KevDue.Value, NpgsqlDbType.Date, ct);
                    await copy.WriteAsync(m.Source, NpgsqlDbType.Text, ct);
                    if (m.Kb is null) await copy.WriteNullAsync(ct); else await copy.WriteAsync(m.Kb, NpgsqlDbType.Text, ct);
                    if (m.Installed is null) await copy.WriteNullAsync(ct); else await copy.WriteAsync(m.Installed, NpgsqlDbType.Text, ct);
                }
                await copy.CompleteAsync(ct);
            }
            await using (var upsert = new NpgsqlCommand("""
                WITH up AS (
                    INSERT INTO zentriq.vuln_findings AS f (asset_id, cve_id, software_name, software_version, vendor, product, fixed_version, status, priority, risk_score,
                                                           first_seen_at, last_seen_at, due_at, source, kb, installed_version)
                    SELECT n.asset_id, n.cve_id, n.name, n.version, n.vendor, n.product, n.fixed, 'open', n.priority, n.risk, now(), now(),
                           LEAST(now() + make_interval(days => CASE n.severity WHEN 'critical' THEN @dc WHEN 'high' THEN @dh WHEN 'medium' THEN @dm ELSE @dl END),
                                 n.kev_due::timestamptz + interval '1 day' - interval '1 second'),
                           n.source, n.kb, n.installed
                      FROM vf_new n
                    ON CONFLICT (asset_id, cve_id, software_name, software_version) DO UPDATE SET
                        last_seen_at = now(), priority = EXCLUDED.priority, risk_score = EXCLUDED.risk_score, fixed_version = EXCLUDED.fixed_version,
                        kb = EXCLUDED.kb, installed_version = EXCLUDED.installed_version, source = EXCLUDED.source,
                        status = CASE WHEN f.status = 'fixed' THEN 'open' ELSE f.status END,
                        status_changed_at = CASE WHEN f.status = 'fixed' THEN now() ELSE f.status_changed_at END,
                        fixed_at = CASE WHEN f.status = 'fixed' THEN NULL ELSE f.fixed_at END,
                        first_seen_at = CASE WHEN f.status = 'fixed' THEN now() ELSE f.first_seen_at END,
                        due_at = CASE WHEN f.status = 'fixed' THEN EXCLUDED.due_at ELSE LEAST(f.due_at, EXCLUDED.due_at) END
                    RETURNING f.id, (xmax = 0) AS inserted, f.first_seen_at >= @started AS fresh),
                ev AS (
                    INSERT INTO zentriq.vuln_finding_events (finding_id, kind, text)
                    SELECT id, 'system', 'Detected again after it had been fixed; reopened.' FROM up WHERE NOT inserted AND fresh)
                SELECT count(*) FILTER (WHERE inserted), count(*) FILTER (WHERE NOT inserted AND fresh) FROM up
                """, conn, tx))
            {
                upsert.Parameters.AddWithValue("dc", sla.Critical);
                upsert.Parameters.AddWithValue("dh", sla.High);
                upsert.Parameters.AddWithValue("dm", sla.Medium);
                upsert.Parameters.AddWithValue("dl", sla.Low);
                upsert.Parameters.AddWithValue("started", started);
                await using var r = await upsert.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { added = (int)r.GetInt64(0); reopened = (int)r.GetInt64(1); }
            }
            await using (var fix = new NpgsqlCommand("""
                WITH fx AS (
                    UPDATE zentriq.vuln_findings f SET status = 'fixed', fixed_at = now(), status_changed_at = now()
                      FROM zentriq.assets a
                     WHERE a.id = f.asset_id AND a.status = 'active' AND f.status IN ('open','in_progress','accepted') AND f.last_seen_at < @started
                       AND NOT (f.source = 'os' AND f.asset_id = ANY(@keep))
                    RETURNING f.id, f.source)
                INSERT INTO zentriq.vuln_finding_events (finding_id, kind, text)
                SELECT id, 'system', CASE WHEN source = 'os' THEN 'Fixed: the device now has the Windows update that fixes it.'
                                          ELSE 'Fixed: the vulnerable version is no longer installed.' END FROM fx
                """, conn, tx))
            {
                fix.Parameters.AddWithValue("started", started);
                fix.Parameters.Add(new NpgsqlParameter("keep", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = keepOs.ToArray() });
                fixedCount = await fix.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        await using var openCmd = ds.CreateCommand("SELECT count(*) FROM zentriq.vuln_findings WHERE status IN ('open','in_progress')");
        var open = Convert.ToInt32(await openCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        return new EvaluationResult(namesByProduct.Count, installs, open, added, fixedCount, reopened) { OsAssessed = osAssessed, OsNotAssessed = osNotAssessed };
    }

    private static NpgsqlParameter Txt(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = (object?)v ?? DBNull.Value };
    private static NpgsqlParameter Ts(string n, DateTimeOffset? v) => new(n, NpgsqlDbType.TimestampTz) { Value = (object?)v ?? DBNull.Value };
    private static NpgsqlParameter Day(string n, DateOnly? v) => new(n, NpgsqlDbType.Date) { Value = (object?)v ?? DBNull.Value };
}
