using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zentriq.Infrastructure.Vulnerabilities;

/// <summary>A fix for one Windows product: devices on the same build line below <see cref="FixedBuild"/> are vulnerable.</summary>
public sealed record MsrcFix(string Product, string FixedBuild, string? Kb, string? KbUrl, string? Supersedes, bool? Restart);

public sealed class MsrcCve
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public decimal? CvssScore { get; init; }
    public string? CvssVector { get; init; }
    public string? MsSeverity { get; init; }
    public bool Exploited { get; init; }
    public bool Disclosed { get; init; }
    public DateTimeOffset? Published { get; init; }
    public List<string> Cwes { get; init; } = [];
    public List<MsrcFix> Fixes { get; init; } = [];
}

/// <summary>One monthly Microsoft Security Update document (CVRF), reduced to Windows operating-system fixes.</summary>
public sealed record MsrcDocument(string Id, string? Title, DateTimeOffset? CurrentRelease, List<MsrcCve> Cves);

public sealed record MsrcUpdateInfo(string Id, DateTimeOffset? CurrentRelease);

/// <summary>
/// Microsoft Security Response Center data: the CVRF v3 API (https://api.msrc.microsoft.com/cvrf/v3.0/) publishes one
/// document per Patch Tuesday with, for every CVE and Windows product, the update (KB) and the <b>fixed build</b>
/// (e.g. 10.0.17763.6893). A Windows device is vulnerable when its build (version + UBR) on the same build line is lower.
/// This is how established scanners assess Windows without credentials to the update history.
/// </summary>
public static partial class Msrc
{
    public const string Api = "https://api.msrc.microsoft.com/cvrf/v3.0";

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^\d+\.\d+\.\d+\.\d+$")]
    private static partial Regex FourPart();

    /// <summary>Document id for a month, e.g. 2026-Oct.</summary>
    public static string MonthId(DateTime month) => month.ToString("yyyy-MMM", CultureInfo.InvariantCulture);

    /// <summary>Matching key for a product name: lower case, single spaces ("Windows 11 version 21H2" = "Windows 11 Version 21H2").</summary>
    public static string Key(string product) => Spaces().Replace(product.Trim(), " ").ToLowerInvariant();

    /// <summary>Windows operating-system products (the ones a device inventory can be matched against).</summary>
    public static bool IsWindowsOs(string product)
    {
        var p = product.TrimStart();
        return p.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Windows 11", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("Windows Server 20", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Windows 8.1", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("Windows 7 for", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Windows RT 8.1", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The MSRC product name for a device, from its inventory: Windows version (10.0.build), whether it is a server,
    /// architecture and installation type. Null when the build is not one Microsoft publishes fixes for.
    /// </summary>
    public static string? ProductFor(string? version, bool? isServer, string? architecture, string? installationType, string? osName = null)
    {
        if (!Version.TryParse(version, out var v)) return null;
        var server = isServer ?? (osName?.Contains("Server", StringComparison.OrdinalIgnoreCase) ?? false);
        var core = installationType?.Contains("Core", StringComparison.OrdinalIgnoreCase) == true;
        var arch = architecture ?? "";
        var archName = arch.Contains("ARM", StringComparison.OrdinalIgnoreCase) ? "ARM64-based Systems"
            : arch.Contains("32", StringComparison.Ordinal) ? "32-bit Systems" : "x64-based Systems";
        string? WithCore(string name) => core ? name + " (Server Core installation)" : name;

        if (v.Major == 6)
        {
            return (v.Minor, v.Build) switch
            {
                (3, 9600) => server ? WithCore("Windows Server 2012 R2") : $"Windows 8.1 for {(archName == "x64-based Systems" ? "x64-based systems" : archName == "32-bit Systems" ? "32-bit systems" : archName)}",
                (2, 9200) => server ? WithCore("Windows Server 2012") : null,
                (1, 7601) => server ? (core ? "Windows Server 2008 R2 for x64-based Systems Service Pack 1 (Server Core installation)" : "Windows Server 2008 R2 for x64-based Systems Service Pack 1")
                                    : $"Windows 7 for {archName} Service Pack 1",
                _ => null,
            };
        }
        if (v.Major != 10) return null;
        if (server)
        {
            return v.Build switch
            {
                14393 => WithCore("Windows Server 2016"),
                17763 => WithCore("Windows Server 2019"),
                20348 => WithCore("Windows Server 2022"),
                25398 => "Windows Server 2022, 23H2 Edition (Server Core installation)",
                26100 => WithCore("Windows Server 2025"),
                _ => null,
            };
        }
        var release = v.Build switch
        {
            10240 => "",
            14393 => "Version 1607 ",
            17763 => "Version 1809 ",
            19044 => "Version 21H2 ",
            19045 => "Version 22H2 ",
            22000 => "Version 21H2 ",
            22621 => "Version 22H2 ",
            22631 => "Version 23H2 ",
            26100 => "Version 24H2 ",
            26200 => "Version 25H2 ",
            _ => null,
        };
        if (release is null) return null;
        return $"Windows {(v.Build >= 22000 ? "11" : "10")} {release}for {archName}";
    }

    /// <summary>Full build "10.0.17763.6893" from version "10.0.17763" and UBR 6893; null without a UBR.</summary>
    public static string? FullBuild(string? version, int? ubr)
    {
        if (ubr is null || !Version.TryParse(version, out var v)) return null;
        return $"{v.Major}.{v.Minor}.{v.Build}.{ubr.Value}";
    }

    /// <summary>"10.0.17763" from "10.0.17763.6893".</summary>
    public static string BaseBuild(string build)
    {
        var parts = build.Split('.');
        return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : build;
    }

    /// <summary>Is a device on <paramref name="deviceBuild"/> missing the fix in <paramref name="fixedBuild"/>? Only within the same build line.</summary>
    public static bool IsVulnerable(string deviceBuild, string fixedBuild) =>
        BaseBuild(deviceBuild) == BaseBuild(fixedBuild) && VersionComparer.Compare(deviceBuild, fixedBuild) < 0;

    // ---- parsing -------------------------------------------------------------------------------------

    /// <summary>The list of published documents (GET /updates): id and current release date.</summary>
    public static List<MsrcUpdateInfo> ParseUpdates(JsonElement root)
    {
        var list = new List<MsrcUpdateInfo>();
        if (Prop(root, "value") is not { ValueKind: JsonValueKind.Array } values) return list;
        foreach (var v in values.EnumerateArray())
            if (Text(Prop(v, "ID")) is { } id)
                list.Add(new MsrcUpdateInfo(id, Date(Text(Prop(v, "CurrentReleaseDate")))));
        return list;
    }

    /// <summary>
    /// Reads a CVRF v3 JSON document. Field names are matched case-insensitively and both numeric and textual type
    /// codes are accepted, so small differences between the API and downloaded files do not break parsing.
    /// </summary>
    public static MsrcDocument ParseDocument(JsonElement root, string? monthHint = null)
    {
        var tracking = Prop(root, "DocumentTracking");
        var id = Text(Prop(Prop(Prop(tracking, "Identification"), "ID"), "Value")) ?? Text(Prop(Prop(tracking, "Identification"), "ID")) ?? monthHint ?? "unknown";
        var title = Text(Prop(Prop(root, "DocumentTitle"), "Value")) ?? Text(Prop(root, "DocumentTitle"));
        var current = Date(Text(Prop(tracking, "CurrentReleaseDate")));

        var products = new Dictionary<string, string>(StringComparer.Ordinal);
        void AddProducts(JsonElement? e)
        {
            if (e is not { } el) return;
            if (el.ValueKind == JsonValueKind.Array) { foreach (var x in el.EnumerateArray()) AddProducts(x); return; }
            if (el.ValueKind != JsonValueKind.Object) return;
            if (Text(Prop(el, "ProductID")) is { } pid && Text(Prop(el, "Value")) is { } name) products[pid] = name.Trim();
            AddProducts(Prop(el, "FullProductName"));
            AddProducts(Prop(el, "Items"));
            AddProducts(Prop(el, "Branch"));
        }
        AddProducts(Prop(root, "ProductTree"));

        var cves = new List<MsrcCve>();
        if (Prop(root, "Vulnerability") is { ValueKind: JsonValueKind.Array } vulns)
            foreach (var v in vulns.EnumerateArray())
            {
                var cveId = Text(Prop(v, "CVE"));
                if (cveId is null || !cveId.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase)) continue;

                decimal? score = null; string? vector = null;
                foreach (var set in Items(Prop(v, "CVSSScoreSets")))
                {
                    var s = Number(Prop(set, "BaseScore"));
                    if (s is not null && (score is null || s > score)) { score = s; vector = Text(Prop(set, "Vector")); }
                }

                string? severity = null; bool exploited = false, disclosed = false;
                foreach (var t in Items(Prop(v, "Threats")))
                {
                    var type = TypeCode(Prop(t, "Type"));
                    var desc = Text(Prop(Prop(t, "Description"), "Value")) ?? "";
                    if (type is 3 || (type is null && desc is "Critical" or "Important" or "Moderate" or "Low"))
                    {
                        if (SeverityRank(desc) > SeverityRank(severity)) severity = desc;
                    }
                    else if (type is 1 || desc.Contains("Exploited:", StringComparison.OrdinalIgnoreCase))
                    {
                        exploited |= desc.Contains("Exploited:Yes", StringComparison.OrdinalIgnoreCase);
                        disclosed |= desc.Contains("Publicly Disclosed:Yes", StringComparison.OrdinalIgnoreCase);
                    }
                }

                string? description = null;
                foreach (var n in Items(Prop(v, "Notes")))
                    if (string.Equals(Text(Prop(n, "Title")), "Description", StringComparison.OrdinalIgnoreCase) && Text(Prop(n, "Value")) is { } html)
                    {
                        description = Clean(html);
                        break;
                    }

                DateTimeOffset? published = null;
                foreach (var rev in Items(Prop(v, "RevisionHistory")))
                    if (Date(Text(Prop(rev, "Date"))) is { } d && (published is null || d < published)) published = d;

                var cwes = new List<string>();
                foreach (var c in Items(Prop(v, "CWE")))
                    if (Text(Prop(c, "ID")) is { } cwe && cwe.StartsWith("CWE-", StringComparison.OrdinalIgnoreCase) && !cwes.Contains(cwe)) cwes.Add(cwe.ToUpperInvariant());

                var fixes = new List<MsrcFix>();
                foreach (var r in Items(Prop(v, "Remediations")))
                {
                    var type = TypeCode(Prop(r, "Type"));
                    if (type is not null and not 2) continue;                       // 2 = Vendor Fix
                    var sub = Text(Prop(r, "SubType")) ?? "";
                    if (sub.Contains("Hotpatch", StringComparison.OrdinalIgnoreCase)) continue;
                    var fixedBuild = Text(Prop(r, "FixedBuild"))?.Trim();
                    if (fixedBuild is null || !FourPart().IsMatch(fixedBuild)) continue;
                    var kbText = Text(Prop(Prop(r, "Description"), "Value")) ?? Text(Prop(r, "Description"));
                    var kb = kbText is null ? null : new string(kbText.Where(char.IsAsciiDigit).ToArray());
                    if (kb is { Length: < 6 or > 8 }) kb = null;
                    var url = Text(Prop(r, "URL"));
                    var restart = Text(Prop(Prop(r, "RestartRequired"), "Value"));
                    foreach (var pid in Strings(Prop(r, "ProductID")))
                    {
                        if (!products.TryGetValue(pid, out var product) || !IsWindowsOs(product)) continue;
                        fixes.Add(new MsrcFix(product, fixedBuild, kb, url is not null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null,
                            Text(Prop(r, "Supercedence")), restart is null ? null : restart.StartsWith("Yes", StringComparison.OrdinalIgnoreCase)));
                    }
                }
                if (fixes.Count == 0) continue;     // not a Windows OS vulnerability (Office, Edge, Azure, …)

                cves.Add(new MsrcCve
                {
                    Id = cveId.ToUpperInvariant(),
                    Title = Text(Prop(Prop(v, "Title"), "Value")) ?? Text(Prop(v, "Title")),
                    Description = description,
                    CvssScore = score,
                    CvssVector = vector,
                    MsSeverity = severity,
                    Exploited = exploited,
                    Disclosed = disclosed,
                    Published = published,
                    Cwes = cwes,
                    Fixes = fixes.DistinctBy(f => (f.Product, f.FixedBuild, f.Kb)).ToList(),
                });
            }
        return new MsrcDocument(id, title, current, cves);
    }

    private static int SeverityRank(string? s) => s switch { "Critical" => 4, "Important" => 3, "Moderate" => 2, "Low" => 1, _ => 0 };

    private static string? Clean(string html)
    {
        var text = System.Net.WebUtility.HtmlDecode(Tags().Replace(html, " "));
        text = Spaces().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text.Length > 4000 ? text[..4000] : text;
    }

    // ---- tolerant JSON helpers -----------------------------------------------------------------------

    private static JsonElement? Prop(JsonElement? e, string name)
    {
        if (e is not { ValueKind: JsonValueKind.Object } o) return null;
        if (o.TryGetProperty(name, out var exact)) return exact;
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static IEnumerable<JsonElement> Items(JsonElement? e)
    {
        if (e is { ValueKind: JsonValueKind.Array } a) return a.EnumerateArray().ToList();
        if (e is { ValueKind: JsonValueKind.Object } o) return new[] { o };
        return Array.Empty<JsonElement>();
    }

    private static IEnumerable<string> Strings(JsonElement? e)
    {
        var list = new List<string>();
        if (e is { ValueKind: JsonValueKind.Array } a)
        {
            foreach (var x in a.EnumerateArray())
                if (Text(x) is { } s) list.Add(s);
        }
        else if (Text(e) is { } one) list.Add(one);
        return list;
    }

    private static string? Text(JsonElement? e) => e switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString() is { Length: > 0 } t ? t : null,
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null,
    };

    private static decimal? Number(JsonElement? e) => e switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetDecimal(out var d) => d,
        { ValueKind: JsonValueKind.String } s when decimal.TryParse(s.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>CVRF type codes arrive as numbers (2) or names ("Vendor Fix", "Severity", "Exploit Status").</summary>
    private static int? TypeCode(JsonElement? e)
    {
        if (e is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var i)) return i;
        return Text(e)?.Replace(" ", "").ToLowerInvariant() switch
        {
            "vendorfix" => 2,
            "severity" => 3,
            "exploitstatus" => 1,
            "impact" => 0,
            null => null,
            var other => int.TryParse(other, out var x) ? x : -1,
        };
    }

    private static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUniversalTime() : null;
}
