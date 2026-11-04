using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.Infrastructure.Deployment;

/// <summary>
/// The standard software catalogue shipped with Zentriq (Resources/software-catalog.json, updated with each release):
/// well-known applications with the names and publishers they install under on Windows, their Linux package names and
/// silent installation switches. Administrators build the organisation's approved software list from it; their own
/// entries are kept in the database (software_catalog).
/// </summary>
public sealed class SoftwareCatalog
{
    public sealed class Entry
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("publisher")] public string? Publisher { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; } = "Other";
        [JsonPropertyName("platforms")] public List<string> Platforms { get; set; } = ["windows"];
        [JsonPropertyName("namePatterns")] public List<string> NamePatterns { get; set; } = [];
        [JsonPropertyName("publisherPatterns")] public List<string> PublisherPatterns { get; set; } = [];
        [JsonPropertyName("website")] public string? Website { get; set; }
        [JsonPropertyName("windowsSilentArgs")] public string? WindowsSilentArgs { get; set; }
        [JsonPropertyName("debPackage")] public string? DebPackage { get; set; }
        [JsonPropertyName("rpmPackage")] public string? RpmPackage { get; set; }
        /// <summary>false for the organisation's own entries.</summary>
        [JsonPropertyName("builtIn")] public bool BuiltIn { get; set; } = true;
        [JsonPropertyName("id")] public Guid? Id { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    private sealed class File
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("entries")] public List<Entry> Entries { get; set; } = [];
    }

    public string Version { get; }
    public IReadOnlyList<Entry> Entries { get; }

    private SoftwareCatalog(string version, List<Entry> entries) { Version = version; Entries = entries; }

    private static readonly Lazy<SoftwareCatalog> Shared = new(Load);
    public static SoftwareCatalog Standard => Shared.Value;

    private static SoftwareCatalog Load()
    {
        using var s = typeof(SoftwareCatalog).Assembly.GetManifestResourceStream("Zentriq.Deployment.SoftwareCatalog.json")
            ?? throw new InvalidOperationException("The standard software catalogue is missing from this build.");
        var f = JsonSerializer.Deserialize<File>(s) ?? new File();
        return new SoftwareCatalog(f.Version, f.Entries);
    }

    public Entry? Find(string key) => Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Categories(IEnumerable<Entry> entries) => entries.Select(e => e.Category).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The approved-list rules for an entry, one per platform: on Windows by the names the software installs under (and
    /// the names its installer goes by) and its publishers; on Linux by its package names.
    /// </summary>
    public static IEnumerable<(string Platform, string Name, string? Publisher)> RulesFor(Entry e, IEnumerable<string>? platforms = null)
    {
        var wanted = platforms?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in e.Platforms.Where(p => wanted is null || wanted.Contains(p)))
        {
            if (p == "linux")
            {
                var names = e.NamePatterns.Where(n => !n.Contains(' ')).Concat(new[] { e.DebPackage, e.RpmPackage }.Where(n => !string.IsNullOrEmpty(n))!)
                    .Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // Package names have no spaces; without a known one, the name in package style ("cisco-secure-client*").
                if (names.Count == 0) names.Add(System.Text.RegularExpressions.Regex.Replace(e.Name.ToLowerInvariant(), "[^a-z0-9+]+", "-").Trim('-') + "*");
                yield return ("linux", string.Join(";", names), null);
            }
            else
            {
                var names = e.NamePatterns.Where(n => n.Contains(' ') || n.Any(char.IsUpper) || n.Contains('*')).ToList();
                // The installer often goes by the product name without the vendor ("Firefox Setup.exe", "Chrome").
                foreach (var n in names.ToList())
                {
                    var first = (e.Publisher ?? "").Split(' ', ',')[0];
                    if (first.Length > 2 && n.StartsWith(first + " ", StringComparison.OrdinalIgnoreCase)) names.Add(n[(first.Length + 1)..]);
                }
                if (names.Count == 0) names.Add(e.Name + "*");
                yield return ("windows", string.Join(";", names.Distinct(StringComparer.OrdinalIgnoreCase)),
                    e.PublisherPatterns.Count > 0 ? string.Join(";", e.PublisherPatterns) : null);
            }
        }
    }
}
