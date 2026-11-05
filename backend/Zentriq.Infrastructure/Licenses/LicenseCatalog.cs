using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zentriq.Infrastructure.Licenses;

/// <summary>Commercial software that is usually licensed, with the names it installs under. Ships with Zentriq.</summary>
public sealed class LicenseCatalog
{
    public sealed class Entry
    {
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("publisher")] public string? Publisher { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; } = "Other";
        /// <summary>device | user | core | concurrent | site</summary>
        [JsonPropertyName("metric")] public string Metric { get; set; } = "device";
        /// <summary>How it is usually sold: perpetual | subscription.</summary>
        [JsonPropertyName("license")] public string License { get; set; } = "perpetual";
        [JsonPropertyName("namePatterns")] public List<string> NamePatterns { get; set; } = [];
        [JsonPropertyName("excludePatterns")] public List<string> ExcludePatterns { get; set; } = [];
        [JsonPropertyName("publisherPatterns")] public List<string> PublisherPatterns { get; set; } = [];
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("products")] public List<Entry> Products { get; set; } = [];

    private static readonly Lazy<LicenseCatalog> Shared = new(() =>
    {
        using var s = typeof(LicenseCatalog).Assembly.GetManifestResourceStream("Zentriq.Licenses.Catalog.json")
                      ?? throw new InvalidOperationException("The licence catalogue is missing from the build.");
        return JsonSerializer.Deserialize<LicenseCatalog>(s) ?? new LicenseCatalog();
    });

    public static LicenseCatalog Standard => Shared.Value;

    public Entry? Find(string key) => Products.FirstOrDefault(p => p.Key == key);

    /// <summary>Whether an installed program counts as this product (the same rule the database applies).</summary>
    public static bool Matches(Entry e, string name, string? publisher) =>
        e.NamePatterns.Any(p => LicenseCompliance.Like(name, p))
        && !e.ExcludePatterns.Any(p => LicenseCompliance.Like(name, p))
        && (e.PublisherPatterns.Count == 0 || (publisher is not null && e.PublisherPatterns.Any(p => LicenseCompliance.Like(publisher, p))));
}
