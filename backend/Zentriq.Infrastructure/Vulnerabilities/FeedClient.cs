using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Zentriq.Infrastructure.Vulnerabilities;

public sealed class FeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads from the public sources (HTTPS, system proxy). NVD rate limits: 5 requests per 30 s without an API key,
/// 50 with one — the client paces itself accordingly and retries 403/429/503 with back-off.
/// </summary>
public sealed class FeedClient : IDisposable
{
    public const string NvdCves = "https://services.nvd.nist.gov/rest/json/cves/2.0";
    public const string NvdCpes = "https://services.nvd.nist.gov/rest/json/cpes/2.0";
    public const string KevUrl = "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json";
    public const string EpssApi = "https://api.first.org/data/v1/epss";

    private readonly HttpClient _http;
    private readonly string? _nvdKey;
    private DateTimeOffset _nextNvd = DateTimeOffset.MinValue;

    public FeedClient(string? nvdApiKey, HttpMessageHandler? handler = null)
    {
        _nvdKey = string.IsNullOrWhiteSpace(nvdApiKey) ? null : nvdApiKey.Trim();
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials })
            : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(3);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zentriq", "1.1"));
    }

    private TimeSpan NvdGap => _nvdKey is null ? TimeSpan.FromSeconds(6.5) : TimeSpan.FromSeconds(0.7);

    private async Task<JsonDocument> GetJsonAsync(string url, bool nvd, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (nvd)
            {
                var wait = _nextNvd - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _nextNvd = DateTimeOffset.UtcNow + NvdGap;
            }
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (nvd && _nvdKey is not null) req.Headers.Add("apiKey", _nvdKey);
            HttpResponseMessage resp;
            try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(10 * attempt), ct); continue; }
                throw new FeedException($"Could not reach {new Uri(url).Host}: {ex.Message}. The server needs outbound HTTPS to this site (or use Import feed files).", ex);
            }
            using (resp)
            {
                if ((resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout) && attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30 * attempt), ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                    throw new FeedException($"{new Uri(url).Host} answered {(int)resp.StatusCode} {resp.ReasonPhrase}{(nvd && resp.StatusCode == HttpStatusCode.NotFound ? " (check the NVD API key)" : "")}.");
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(s, cancellationToken: ct);
            }
        }
    }

    /// <summary>All CVEs whose configurations match vendor:product, optionally only those modified since a date (max 120-day windows).</summary>
    public async IAsyncEnumerable<List<CveRecord>> NvdCvesForProductAsync(string vendor, string product, DateTimeOffset? modifiedSince,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var windows = new List<(DateTimeOffset From, DateTimeOffset To)?>();
        if (modifiedSince is { } since)
        {
            var end = DateTimeOffset.UtcNow;
            for (var from = since.AddHours(-1); from < end; from = from.AddDays(119))
                windows.Add((from, from.AddDays(119) < end ? from.AddDays(119) : end));
        }
        else windows.Add(null);
        foreach (var w in windows)
        {
            var start = 0;
            while (true)
            {
                var url = $"{NvdCves}?virtualMatchString={Uri.EscapeDataString(Cpe.MatchString(vendor, product))}&resultsPerPage=2000&startIndex={start}";
                if (w is { } win)
                    url += $"&lastModStartDate={Uri.EscapeDataString(win.From.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture))}%2B00:00" +
                           $"&lastModEndDate={Uri.EscapeDataString(win.To.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture))}%2B00:00";
                using var doc = await GetJsonAsync(url, true, ct);
                var (cves, total) = FeedParsers.ParseNvd(doc.RootElement);
                yield return cves;
                start += 2000;
                if (start >= total || cves.Count == 0) break;
            }
        }
    }

    /// <summary>NVD product dictionary search: distinct vendor:product pairs for a keyword, with a title.</summary>
    public async Task<List<(string Vendor, string Product, string Title)>> SearchProductsAsync(string keyword, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{NvdCpes}?keywordSearch={Uri.EscapeDataString(keyword)}&resultsPerPage=200", true, ct);
        var list = new List<(string, string, string)>();
        if (!doc.RootElement.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in products.EnumerateArray())
        {
            if (!p.TryGetProperty("cpe", out var cpe)) continue;
            if (cpe.TryGetProperty("deprecated", out var dep) && dep.ValueKind == JsonValueKind.True) continue;
            var name = cpe.TryGetProperty("cpeName", out var n) ? n.GetString() : null;
            if (Cpe.Parse(name) is not { Part: "a" } c) continue;
            if (list.Any(x => x.Item1 == c.Vendor && x.Item2 == c.Product)) continue;
            var title = "";
            if (cpe.TryGetProperty("titles", out var titles) && titles.ValueKind == JsonValueKind.Array)
                foreach (var t in titles.EnumerateArray())
                    if (t.TryGetProperty("lang", out var l) && l.GetString() == "en" && t.TryGetProperty("title", out var tt)) { title = tt.GetString() ?? ""; break; }
            list.Add((c.Vendor, c.Product, title));
            if (list.Count >= 25) break;
        }
        return list;
    }

    public async Task<List<KevEntry>> KevAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(KevUrl, false, ct);
        return FeedParsers.ParseKev(doc.RootElement);
    }

    /// <summary>EPSS scores for up to 100 CVEs per request.</summary>
    public async Task<List<EpssEntry>> EpssAsync(IReadOnlyList<string> cves, CancellationToken ct)
    {
        var all = new List<EpssEntry>();
        foreach (var chunk in cves.Chunk(100))
        {
            using var doc = await GetJsonAsync($"{EpssApi}?cve={string.Join(',', chunk)}&limit=100", false, ct);
            all.AddRange(FeedParsers.ParseEpssJson(doc.RootElement));
        }
        return all;
    }

    /// <summary>Opens an uploaded file, transparently un-gzipping it.</summary>
    public static Stream OpenMaybeGzip(byte[] content) =>
        content.Length > 2 && content[0] == 0x1f && content[1] == 0x8b
            ? new GZipStream(new MemoryStream(content), CompressionMode.Decompress)
            : new MemoryStream(content);

    public void Dispose() => _http.Dispose();
}
