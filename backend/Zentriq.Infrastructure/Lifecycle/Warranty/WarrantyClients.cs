using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Zentriq.Infrastructure.Lifecycle.Warranty;

/// <summary>One warranty or service entitlement reported by the manufacturer.</summary>
public sealed record WarrantyEntitlement(string? Name, DateOnly? Start, DateOnly? End, string? Type);

/// <summary>The manufacturer's answer for one serial number.</summary>
public sealed record WarrantyAnswer(string Serial, bool Found, string? Product, DateOnly? ShipDate, IReadOnlyList<WarrantyEntitlement> Entitlements, string? Message = null)
{
    /// <summary>The cover that ends last (the device's warranty end) and the earliest start.</summary>
    public WarrantyEntitlement? Latest => Entitlements.Where(e => e.End is not null).OrderByDescending(e => e.End).FirstOrDefault();
    public DateOnly? Start => Entitlements.Where(e => e.Start is not null).Select(e => e.Start).Min();
    public DateOnly? End => Latest?.End;
}

/// <summary>A vendor API connection: client id and secret (Dell, HP) or client id (Lenovo), and the base address.</summary>
public sealed record WarrantyProviderConfig(string Vendor, string ClientId, string? Secret, string? ApiUrl);

public sealed class WarrantyApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Clients for the manufacturers' warranty APIs:
/// <list type="bullet">
/// <item>Dell TechDirect (Warranty API v5): OAuth 2 client credentials, asset-entitlements by service tag (100 per call);</item>
/// <item>Lenovo (PSREF / Support warranty API): ClientID header, serial numbers comma separated;</item>
/// <item>HP (HP Developer Portal, Warranty API): OAuth 2 client credentials, serial number with product number.</item>
/// </list>
/// The answers are read tolerantly (field names differ between API versions); dates are taken as calendar days.
/// </summary>
public static class WarrantyClients
{
    public static readonly string[] Vendors = ["dell", "lenovo", "hp"];

    public static string Label(string vendor) => vendor switch { "dell" => "Dell", "lenovo" => "Lenovo", "hp" => "HP", _ => vendor };

    public static string DefaultUrl(string vendor) => vendor switch
    {
        "dell" => "https://apigtwb2c.us.dell.com",
        "lenovo" => "https://supportapi.lenovo.com/v2.5/warranty",
        "hp" => "https://warranty.api.hp.com",
        _ => throw new ArgumentOutOfRangeException(nameof(vendor)),
    };

    /// <summary>How many serial numbers one request may carry.</summary>
    public static int BatchSize(string vendor) => vendor switch { "dell" => 100, "lenovo" => 25, _ => 20 };

    /// <summary>The vendor whose API covers a device of this manufacturer, or null (HPE servers and others are not covered).</summary>
    public static string? Detect(string? manufacturer)
    {
        var m = (manufacturer ?? "").Trim().ToLowerInvariant();
        if (m.Length == 0) return null;
        if (m.Contains("dell")) return "dell";
        if (m.Contains("lenovo")) return "lenovo";
        if (m.Contains("enterprise")) return null; // Hewlett Packard Enterprise: a different service
        if (m == "hp" || m.StartsWith("hp ", StringComparison.Ordinal) || m.StartsWith("hp-", StringComparison.Ordinal) || m.Contains("hewlett")) return "hp";
        return null;
    }

    /// <summary>A serial number the vendors accept: letters and digits only (service tags are 7 characters at Dell).</summary>
    public static string? CleanSerial(string? serial)
    {
        var s = new string((serial ?? "").Trim().Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (s.Length is < 4 or > 30) return null;
        if (s is "DEFAULTSTRING" or "TOBEFILLEDBYOEM" or "SYSTEMSERIALNUMBER" or "0123456789" or "NONE" or "NA") return null;
        if (s.Distinct().Count() == 1) return null;   // 00000000
        return s;
    }

    /// <summary>The HP product number without the localisation suffix ("2Z6A6EA#ABU" → "2Z6A6EA").</summary>
    public static string? HpProductNumber(string? sku)
    {
        var s = (sku ?? "").Trim();
        var hash = s.IndexOf('#');
        if (hash > 0) s = s[..hash];
        s = new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return s.Length is >= 4 and <= 20 ? s : null;
    }

    public static async Task<Dictionary<string, WarrantyAnswer>> LookupAsync(HttpClient http, WarrantyProviderConfig cfg, IReadOnlyList<(string Serial, string? Sku)> items, CancellationToken ct)
    {
        if (items.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        return cfg.Vendor switch
        {
            "dell" => await DellAsync(http, cfg, items.Select(i => i.Serial).ToList(), ct),
            "lenovo" => await LenovoAsync(http, cfg, items.Select(i => i.Serial).ToList(), ct),
            "hp" => await HpAsync(http, cfg, items, ct),
            _ => throw new WarrantyApiException("Unknown vendor " + cfg.Vendor),
        };
    }

    private static string Base(WarrantyProviderConfig cfg) => (string.IsNullOrWhiteSpace(cfg.ApiUrl) ? DefaultUrl(cfg.Vendor) : cfg.ApiUrl.Trim()).TrimEnd('/');

    // ---- Dell ----------------------------------------------------------------------------------------------------

    /// <summary>Dell entitlements that are not hardware cover (Dell Digital Delivery, ProSupport add-ons for software…).</summary>
    private static readonly HashSet<string> DellIgnored = new(StringComparer.OrdinalIgnoreCase) { "D", "DL", "PJ", "PR" };

    private static async Task<Dictionary<string, WarrantyAnswer>> DellAsync(HttpClient http, WarrantyProviderConfig cfg, List<string> serials, CancellationToken ct)
    {
        var b = Base(cfg);
        var token = await ClientCredentialsAsync(http, $"{b}/auth/oauth/v2/token", cfg, ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{b}/PROD/sbil/eapi/v5/asset-entitlements?servicetags={Uri.EscapeDataString(string.Join(",", serials))}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var doc = await SendJsonAsync(http, req, "Dell", ct);
        var result = new Dictionary<string, WarrantyAnswer>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Items(doc.RootElement))
        {
            var tag = Str(e, "serviceTag") ?? Str(e, "servicetag");
            if (tag is null) continue;
            if (Bool(e, "invalid") == true)
            {
                result[tag] = new WarrantyAnswer(tag, false, null, null, [], "Dell does not know this service tag.");
                continue;
            }
            var ents = new List<WarrantyEntitlement>();
            if (e.TryGetProperty("entitlements", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var x in arr.EnumerateArray())
                {
                    if (Str(x, "serviceLevelCode") is { } code && DellIgnored.Contains(code)) continue;
                    ents.Add(new WarrantyEntitlement(Str(x, "serviceLevelDescription") ?? Str(x, "serviceLevelCode"), Day(x, "startDate"), Day(x, "endDate"), Str(x, "entitlementType")));
                }
            result[tag] = new WarrantyAnswer(tag, true, Str(e, "productLineDescription") ?? Str(e, "systemDescription") ?? Str(e, "productLobDescription"), Day(e, "shipDate"), ents);
        }
        return result;
    }

    // ---- Lenovo --------------------------------------------------------------------------------------------------

    private static async Task<Dictionary<string, WarrantyAnswer>> LenovoAsync(HttpClient http, WarrantyProviderConfig cfg, List<string> serials, CancellationToken ct)
    {
        var b = Base(cfg);
        var sep = b.Contains('?') ? "&" : "?";
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{b}{sep}Serial={Uri.EscapeDataString(string.Join(",", serials))}");
        req.Headers.TryAddWithoutValidation("ClientID", cfg.ClientId);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var doc = await SendJsonAsync(http, req, "Lenovo", ct);
        var result = new Dictionary<string, WarrantyAnswer>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Items(doc.RootElement))
        {
            var serial = Str(e, "Serial") ?? (serials.Count == 1 ? serials[0] : null);
            if (serial is null) continue;
            var ents = new List<WarrantyEntitlement>();
            foreach (var key in new[] { "Warranty", "UpmaWarranty", "Contract" })
                if (e.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var x in arr.EnumerateArray())
                        ents.Add(new WarrantyEntitlement(Str(x, "Name") ?? Str(x, "Description"), Day(x, "Start"), Day(x, "End"), Str(x, "Type")));
            var error = Str(e, "ErrorMessage") ?? Str(e, "Message");
            var found = ents.Count > 0 || Str(e, "Product") is not null;
            result[serial] = new WarrantyAnswer(serial, found, Str(e, "Product") ?? Str(e, "ProductName"), Day(e, "Shipped") ?? Day(e, "Purchased"), ents,
                found ? null : error ?? "Lenovo does not know this serial number.");
        }
        return result;
    }

    // ---- HP ------------------------------------------------------------------------------------------------------

    private static async Task<Dictionary<string, WarrantyAnswer>> HpAsync(HttpClient http, WarrantyProviderConfig cfg, IReadOnlyList<(string Serial, string? Sku)> items, CancellationToken ct)
    {
        var b = Base(cfg);
        var token = await ClientCredentialsAsync(http, $"{b}/oauth/v1/token", cfg, ct);
        var body = JsonSerializer.Serialize(items.Select(i => HpProductNumber(i.Sku) is { } pn
            ? new Dictionary<string, string> { ["sn"] = i.Serial, ["pn"] = pn }
            : new Dictionary<string, string> { ["sn"] = i.Serial }));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{b}/productwarranty/v2/queries") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var doc = await SendJsonAsync(http, req, "HP", ct);
        var result = new Dictionary<string, WarrantyAnswer>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Items(doc.RootElement))
        {
            var serial = Str(e, "serialNumber") ?? Str(e, "sn") ?? (items.Count == 1 ? items[0].Serial : null);
            if (serial is null) continue;
            var ents = new List<WarrantyEntitlement>();
            Collect(e, ents, 0);
            var status = Str(e, "status") ?? Str(e, "warrantyStatus");
            var found = ents.Count > 0;
            result[serial] = new WarrantyAnswer(serial, found, Str(e, "productDescription") ?? Str(e, "productName") ?? Str(e, "productNumber"), Day(e, "shipDate"), ents,
                found ? null : Str(e, "message") ?? Str(e, "errorMessage") ?? (status is null ? "HP does not know this serial number (a product number may be needed)." : $"HP: {status}"));
        }
        return result;
    }

    /// <summary>Every object below <paramref name="e"/> with a start and an end date (HP calls them offers / obligations).</summary>
    internal static void Collect(JsonElement e, List<WarrantyEntitlement> into, int depth)
    {
        if (depth > 6) return;
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Collect(x, into, depth + 1); return; }
        if (e.ValueKind != JsonValueKind.Object) return;
        DateOnly? start = null, end = null; string? name = null, type = null;
        foreach (var p in e.EnumerateObject())
        {
            var k = p.Name.ToLowerInvariant();
            if (p.Value.ValueKind == JsonValueKind.String)
            {
                if (k.Contains("start") && k.Contains("date") && start is null) start = ParseDay(p.Value.GetString());
                else if (k.Contains("end") && k.Contains("date") && end is null) end = ParseDay(p.Value.GetString());
                else if (name is null && (k.EndsWith("description", StringComparison.Ordinal) || k == "name" || k.EndsWith("name", StringComparison.Ordinal) && k.Contains("offer"))) name = p.Value.GetString();
                else if (type is null && k.Contains("type")) type = p.Value.GetString();
            }
        }
        if (end is not null) into.Add(new WarrantyEntitlement(name, start, end, type));
        foreach (var p in e.EnumerateObject())
            if (p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) Collect(p.Value, into, depth + 1);
    }

    // ---- shared --------------------------------------------------------------------------------------------------

    private static async Task<string> ClientCredentialsAsync(HttpClient http, string url, WarrantyProviderConfig cfg, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.ClientId}:{cfg.Secret}")));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var doc = await SendJsonAsync(http, req, Label(cfg.Vendor) + " sign-in", ct);
        return Str(doc.RootElement, "access_token") ?? throw new WarrantyApiException($"{Label(cfg.Vendor)} did not issue an access token. Check the client ID and secret.");
    }

    private static async Task<JsonDocument> SendJsonAsync(HttpClient http, HttpRequestMessage req, string what, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new WarrantyApiException($"{what}: cannot reach {req.RequestUri?.Host} ({ex.Message}). The server needs internet access (or a proxy).", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new WarrantyApiException($"{what}: {req.RequestUri?.Host} did not answer in time.", ex); }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = text.Length > 300 ? text[..300] : text;
                throw new WarrantyApiException((int)resp.StatusCode switch
                {
                    401 or 403 => $"{what}: the API refused the key (HTTP {(int)resp.StatusCode}). Check the client ID / secret and that the key is approved for the warranty API.",
                    429 => $"{what}: too many requests (HTTP 429). Lookups continue later.",
                    _ => $"{what}: HTTP {(int)resp.StatusCode} {detail}".Trim(),
                });
            }
            try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "[]" : text); }
            catch (JsonException ex) { throw new WarrantyApiException($"{what}: the answer is not JSON.", ex); }
        }
    }

    /// <summary>The result objects of an answer: an array, an object with an array property, or one object.</summary>
    internal static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) { foreach (var x in root.EnumerateArray()) if (x.ValueKind == JsonValueKind.Object) yield return x; yield break; }
        if (root.ValueKind != JsonValueKind.Object) yield break;
        foreach (var key in new[] { "data", "results", "items", "value" })
            if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in arr.EnumerateArray()) if (x.ValueKind == JsonValueKind.Object) yield return x;
                yield break;
            }
        yield return root;
    }

    internal static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind switch
                {
                    JsonValueKind.String => string.IsNullOrWhiteSpace(p.Value.GetString()) ? null : p.Value.GetString()!.Trim(),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    _ => null,
                };
        return null;
    }

    private static bool? Bool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        return null;
    }

    private static DateOnly? Day(JsonElement e, string name) => ParseDay(Str(e, name));

    /// <summary>The calendar day of an ISO date or date-time ("2027-03-14T05:59:59.000Z" → 2027-03-14).</summary>
    public static DateOnly? ParseDay(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        var t = s.IndexOf('T');
        var datePart = t >= 10 ? s[..t] : s.Length >= 10 && s[4] == '-' ? s[..10] : s;
        if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) return DateOnly.FromDateTime(dto.UtcDateTime);
        return null;
    }
}
