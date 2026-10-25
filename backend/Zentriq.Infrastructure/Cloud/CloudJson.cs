using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Zentriq.Infrastructure.Cloud;

/// <summary>Small helpers to read the platforms' JSON answers without failing on missing or differently typed fields.</summary>
public static class CloudJson
{
    public static JsonElement Get(JsonElement e, params string[] path)
    {
        foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return default; }
        return e;
    }

    public static string? Str(JsonElement e, params string[] path)
    {
        var v = Get(e, path);
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => null,
        };
    }

    public static long? Long(JsonElement e, params string[] path)
    {
        var v = Get(e, path);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.Number) return (long)v.GetDouble();
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    public static bool? Bool(JsonElement e, params string[] path)
    {
        var v = Get(e, path);
        return v.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
            _ => null,
        };
    }

    public static IEnumerable<JsonElement> Arr(JsonElement e, params string[] path)
    {
        var v = Get(e, path);
        return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
    }

    public static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1970 ? d : null;

    public static DateTimeOffset? Epoch(long? seconds) => seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
    public static DateTimeOffset? EpochMs(long? ms) => ms is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms.Value) : null;

    public static decimal Gb(long bytes) => Math.Round(bytes / 1073741824m, 1);

    /// <summary>The last segment of a URL or path ("…/machineTypes/e2-medium" → "e2-medium").</summary>
    public static string? Last(string? s) => s is null ? null : s.TrimEnd('/')[(s.TrimEnd('/').LastIndexOf('/') + 1)..];

    /// <summary>GET/POST returning JSON, with a readable error for unreachable hosts, certificates and HTTP failures.</summary>
    public static async Task<JsonElement> SendAsync(HttpClient http, HttpRequestMessage req, string what, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            throw new CloudException($"The certificate of {req.RequestUri!.Host} is not trusted. Install its root certificate on the Zentriq server, or tick 'Skip the certificate check'.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new CloudException(CloudHttp.Unreachable(req.RequestUri!.Host, ex), ex);
        }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(text)) return default;
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }
            throw new CloudHttpException(resp.StatusCode, what, text);
        }
    }
}

/// <summary>An HTTP error answer from a platform API, with the platform's own message when it gives one.</summary>
public sealed class CloudHttpException(HttpStatusCode status, string what, string body) : CloudException(Describe(status, what, body))
{
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;

    private static string Describe(HttpStatusCode status, string what, string body)
    {
        string? msg = null;
        try
        {
            using var j = JsonDocument.Parse(body);
            var r = j.RootElement;
            msg = CloudJson.Str(r, "error", "message") ?? CloudJson.Str(r, "message") ?? CloudJson.Str(r, "error_description")
                  ?? CloudJson.Str(r, "errors") ?? CloudJson.Str(r, "detail") ?? CloudJson.Str(r, "fault", "detail")
                  ?? (CloudJson.Get(r, "message_list").ValueKind == JsonValueKind.Array ? CloudJson.Str(CloudJson.Arr(r, "message_list").FirstOrDefault(), "message") : null);
        }
        catch (JsonException) { msg = body.Length > 200 ? body[..200] : body; }
        var head = status switch
        {
            HttpStatusCode.Unauthorized => "the user name, password or token was rejected",
            HttpStatusCode.Forbidden => "permission missing",
            HttpStatusCode.NotFound => "not found",
            _ => $"HTTP {(int)status}",
        };
        return $"{what}: {head}{(string.IsNullOrWhiteSpace(msg) ? "" : " — " + msg.Trim())}";
    }
}
