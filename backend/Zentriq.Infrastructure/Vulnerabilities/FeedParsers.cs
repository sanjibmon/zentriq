using System.Globalization;
using System.Text.Json;

namespace Zentriq.Infrastructure.Vulnerabilities;

public sealed record CpeRule(string Part, string Vendor, string Product, string? Version, string? StartIncl, string? StartExcl, string? EndIncl, string? EndExcl, string Criteria);

public sealed class CveRecord
{
    public required string Id { get; init; }
    public DateTimeOffset? Published { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string? Status { get; init; }
    public string? Description { get; init; }
    public string? CvssVersion { get; init; }
    public decimal? CvssScore { get; init; }
    public string? CvssVector { get; init; }
    public string? Severity { get; init; }
    public List<string> Cwes { get; init; } = [];
    public List<string> References { get; init; } = [];
    public DateOnly? KevAdded { get; init; }
    public DateOnly? KevDue { get; init; }
    public string? KevAction { get; init; }
    public List<CpeRule> Rules { get; init; } = [];
}

public sealed record KevEntry(string CveId, DateOnly? Added, DateOnly? Due, string? Ransomware, string? Action);
public sealed record EpssEntry(string CveId, decimal Score, decimal Percentile, DateOnly? Date);

/// <summary>Parsers for the public feeds. They accept both the online API responses and the downloadable files.</summary>
public static class FeedParsers
{
    // ---- NVD CVE API 2.0 / JSON 2.0 feed ------------------------------------------------------------

    /// <summary>Reads an NVD CVE 2.0 document ({ "vulnerabilities": [ { "cve": {...} } ] }); returns the CVEs and totalResults.</summary>
    public static (List<CveRecord> Cves, int Total) ParseNvd(JsonElement root)
    {
        var list = new List<CveRecord>();
        if (root.TryGetProperty("vulnerabilities", out var vulns) && vulns.ValueKind == JsonValueKind.Array)
            foreach (var v in vulns.EnumerateArray())
                if (v.TryGetProperty("cve", out var cve) && Cve(cve) is { } rec) list.Add(rec);
        var total = root.TryGetProperty("totalResults", out var t) && t.TryGetInt32(out var n) ? n : list.Count;
        return (list, total);
    }

    private static CveRecord? Cve(JsonElement c)
    {
        var id = Str(c, "id");
        if (id is null || !id.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase)) return null;
        var (version, score, vector, severity) = BestCvss(c);
        var rules = new List<CpeRule>();
        if (c.TryGetProperty("configurations", out var configs) && configs.ValueKind == JsonValueKind.Array)
            foreach (var cfg in configs.EnumerateArray())
                if (cfg.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                    foreach (var node in nodes.EnumerateArray())
                    {
                        if (node.TryGetProperty("negate", out var neg) && neg.ValueKind == JsonValueKind.True) continue;
                        if (!node.TryGetProperty("cpeMatch", out var matches) || matches.ValueKind != JsonValueKind.Array) continue;
                        foreach (var m in matches.EnumerateArray())
                        {
                            if (!(m.TryGetProperty("vulnerable", out var vul) && vul.ValueKind == JsonValueKind.True)) continue;
                            var criteria = Str(m, "criteria");
                            if (Cpe.Parse(criteria) is not { } cpe) continue;
                            rules.Add(new CpeRule(cpe.Part, cpe.Vendor, cpe.Product, cpe.Version is "" ? null : cpe.Version,
                                Str(m, "versionStartIncluding"), Str(m, "versionStartExcluding"), Str(m, "versionEndIncluding"), Str(m, "versionEndExcluding"), criteria!));
                        }
                    }
        var cwes = new List<string>();
        if (c.TryGetProperty("weaknesses", out var weak) && weak.ValueKind == JsonValueKind.Array)
            foreach (var w in weak.EnumerateArray())
                if (w.TryGetProperty("description", out var ds) && ds.ValueKind == JsonValueKind.Array)
                    foreach (var d in ds.EnumerateArray())
                        if (Str(d, "value") is { } cwe && cwe.StartsWith("CWE-", StringComparison.Ordinal) && !cwes.Contains(cwe)) cwes.Add(cwe);
        var refs = new List<string>();
        if (c.TryGetProperty("references", out var rs) && rs.ValueKind == JsonValueKind.Array)
            foreach (var r in rs.EnumerateArray())
                if (Str(r, "url") is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http") && refs.Count < 25 && !refs.Contains(u)) refs.Add(u);
        string? description = null;
        if (c.TryGetProperty("descriptions", out var descs) && descs.ValueKind == JsonValueKind.Array)
            foreach (var d in descs.EnumerateArray())
                if (Str(d, "lang") == "en") { description = Str(d, "value"); break; }
        return new CveRecord
        {
            Id = id.ToUpperInvariant(),
            Published = Date(Str(c, "published")),
            Modified = Date(Str(c, "lastModified")),
            Status = Str(c, "vulnStatus"),
            Description = description,
            CvssVersion = version,
            CvssScore = score,
            CvssVector = vector,
            Severity = severity ?? VulnScoring.SeverityFor(score),
            Cwes = cwes,
            References = refs,
            KevAdded = Day(Str(c, "cisaExploitAdd")),
            KevDue = Day(Str(c, "cisaActionDue")),
            KevAction = Str(c, "cisaRequiredAction"),
            Rules = rules,
        };
    }

    /// <summary>CVSS v3.1 → v3.0 → v4.0 → v2, preferring the NVD ("Primary") score within each version.</summary>
    private static (string? Version, decimal? Score, string? Vector, string? Severity) BestCvss(JsonElement c)
    {
        if (!c.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object) return (null, null, null, null);
        foreach (var key in new[] { "cvssMetricV31", "cvssMetricV30", "cvssMetricV40", "cvssMetricV2" })
        {
            if (!metrics.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) continue;
            JsonElement? pick = null;
            foreach (var m in arr.EnumerateArray()) if (Str(m, "type") == "Primary") { pick = m; break; }
            var metric = pick ?? arr[0];
            if (!metric.TryGetProperty("cvssData", out var data)) continue;
            decimal? score = data.TryGetProperty("baseScore", out var bs) && bs.TryGetDecimal(out var s) ? s : null;
            var sev = Str(data, "baseSeverity") ?? Str(metric, "baseSeverity");
            var fallback = key switch { "cvssMetricV31" => "3.1", "cvssMetricV30" => "3.0", "cvssMetricV40" => "4.0", _ => "2.0" };
            return (Str(data, "version") ?? fallback, score, Str(data, "vectorString"), sev?.ToLowerInvariant());
        }
        return (null, null, null, null);
    }

    // ---- CISA Known Exploited Vulnerabilities ------------------------------------------------------

    public static List<KevEntry> ParseKev(JsonElement root)
    {
        var list = new List<KevEntry>();
        if (!root.TryGetProperty("vulnerabilities", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in arr.EnumerateArray())
            if (Str(v, "cveID") is { } id && id.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
                list.Add(new KevEntry(id.ToUpperInvariant(), Day(Str(v, "dateAdded")), Day(Str(v, "dueDate")), Str(v, "knownRansomwareCampaignUse"), Str(v, "requiredAction")));
        return list;
    }

    // ---- FIRST EPSS ---------------------------------------------------------------------------------

    /// <summary>EPSS API response: { "data": [ { "cve", "epss", "percentile", "date" } ] }.</summary>
    public static List<EpssEntry> ParseEpssJson(JsonElement root)
    {
        var list = new List<EpssEntry>();
        if (!root.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in arr.EnumerateArray())
            if (Str(v, "cve") is { } id && Dec(Str(v, "epss")) is { } e)
                list.Add(new EpssEntry(id.ToUpperInvariant(), e, Dec(Str(v, "percentile")) ?? 0, Day(Str(v, "date"))));
        return list;
    }

    /// <summary>EPSS daily CSV: "#model_version:...,score_date:2026-10-14T00:00:00+0000" then "cve,epss,percentile".</summary>
    public static IEnumerable<EpssEntry> ParseEpssCsv(TextReader reader)
    {
        DateOnly? date = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith('#'))
            {
                var i = line.IndexOf("score_date:", StringComparison.Ordinal);
                if (i >= 0 && line.Length >= i + 21) date = Day(line.Substring(i + 11, 10));
                continue;
            }
            var p = line.Split(',');
            if (p.Length < 3 || !p[0].StartsWith("CVE-", StringComparison.OrdinalIgnoreCase)) continue;
            if (Dec(p[1]) is { } e) yield return new EpssEntry(p[0].Trim().ToUpperInvariant(), e, Dec(p[2]) ?? 0, date);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : null;

    private static DateOnly? Day(string? s) =>
        s is not null && DateOnly.TryParse(s.Length >= 10 ? s[..10] : s, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static decimal? Dec(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? decimal.Round(d, 5) : null;
}
