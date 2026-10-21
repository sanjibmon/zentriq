using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Zentriq.Inventory;

namespace Zentriq.Agent;

internal sealed class EnrollResponse
{
    [JsonPropertyName("agentId")] public string AgentId { get; set; } = "";
    [JsonPropertyName("certificate")] public string Certificate { get; set; } = "";
    [JsonPropertyName("caCertificate")] public string? CaCertificate { get; set; }
    [JsonPropertyName("checkinMinutes")] public int CheckinMinutes { get; set; }
    [JsonPropertyName("inventoryMinutes")] public int InventoryMinutes { get; set; }
    [JsonPropertyName("serverPins")] public List<string>? ServerPins { get; set; }
    [JsonPropertyName("serverUrls")] public List<string>? ServerUrls { get; set; }
    [JsonPropertyName("uninstallProtection")] public bool UninstallProtection { get; set; }
    [JsonPropertyName("uninstallKey")] public string? UninstallKey { get; set; }
}

/// <summary>Answer to /enroll/hello: the server's key pins and its proof that it knows the enrollment code.</summary>
internal sealed class HelloResponse
{
    [JsonPropertyName("pins")] public List<string>? Pins { get; set; }
    [JsonPropertyName("proof")] public string Proof { get; set; } = "";
    [JsonPropertyName("serverUrls")] public List<string>? ServerUrls { get; set; }
}

internal sealed class CheckinResponse
{
    [JsonPropertyName("checkinMinutes")] public int CheckinMinutes { get; set; }
    [JsonPropertyName("inventoryMinutes")] public int InventoryMinutes { get; set; }
    [JsonPropertyName("serverUrls")] public List<string>? ServerUrls { get; set; }
    [JsonPropertyName("uninstallProtection")] public bool UninstallProtection { get; set; }
    [JsonPropertyName("uninstallKey")] public string? UninstallKey { get; set; }
    [JsonPropertyName("inventoryDue")] public bool InventoryDue { get; set; }
    [JsonPropertyName("renewCertificate")] public bool RenewCertificate { get; set; }
    [JsonPropertyName("serverPins")] public List<string>? ServerPins { get; set; }
    [JsonPropertyName("actions")] public List<AgentAction>? Actions { get; set; }
    [JsonPropertyName("uninstallCode")] public UninstallCodeInfo? UninstallCode { get; set; }
}

/// <summary>The uninstall code for all agents: its hash and expiry (the code itself never reaches the agents).</summary>
internal sealed class UninstallCodeInfo
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class PresenceResponse
{
    [JsonPropertyName("wake")] public bool Wake { get; set; }
    [JsonPropertyName("uninstallProtection")] public bool? UninstallProtection { get; set; }
    [JsonPropertyName("uninstallCode")] public UninstallCodeInfo? UninstallCode { get; set; }
}

/// <summary>A device action requested by an administrator in the console (service start/stop/restart).</summary>
internal sealed class AgentAction
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("operation")] public string Operation { get; set; } = "";
    [JsonPropertyName("argument")] public string? Argument { get; set; }
}

internal sealed class RenewResponse
{
    [JsonPropertyName("certificate")] public string Certificate { get; set; } = "";
}

/// <summary>An HTTP error from the server with its problem title (no secrets).</summary>
internal sealed class ServerException : Exception
{
    public HttpStatusCode Status { get; }
    public ServerException(HttpStatusCode status, string message) : base(message) { Status = status; }
}

/// <summary>
/// HTTPS client for the agent port. The server certificate is accepted when it chains to a trusted root with a
/// matching name, or when its public key matches a pin delivered in the enrollment code / check-in.
/// </summary>
internal sealed class ServerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;

    /// <summary>The key pin of the certificate the server presented (set by the first TLS handshake).</summary>
    public string? ObservedPin { get; private set; }

    public string BaseUrl => _base.GetLeftPart(UriPartial.Authority);

    /// <param name="trustAnyForHello">Only for <see cref="HelloAsync"/>: accept any certificate and record its pin; the
    /// caller then checks that the server proved the enrollment code for exactly that pin.</param>
    /// <param name="timeout">Request timeout (short for connectivity checks, long for inventory uploads).</param>
    public ServerClient(string serverUrl, IReadOnlyCollection<string> pins, X509Certificate2? clientCertificate, string? proxyUrl,
        bool trustAnyForHello = false, TimeSpan? timeout = null)
    {
        _base = new Uri(serverUrl.TrimEnd('/') + "/");
        proxyUrl = UsableProxy(proxyUrl, _base);
        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            AutomaticDecompression = DecompressionMethods.GZip,
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (cert is not null) try { ObservedPin = AgentProtocol.SpkiSha256(cert.RawData); } catch (FormatException) { }
                return trustAnyForHello ? cert is not null : Validate(cert, errors, pins);
            },
            UseProxy = !string.IsNullOrEmpty(proxyUrl),
            Proxy = string.IsNullOrEmpty(proxyUrl) ? null : new WebProxy(proxyUrl) { UseDefaultCredentials = true },
        };
        if (clientCertificate is not null) handler.ClientCertificates.Add(clientCertificate);
        _http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZentriqAgent", Program.Version));
    }

    /// <summary>
    /// Only plain http:// forward proxies are supported by .NET Framework. Anything else (an https:// address, or the
    /// Zentriq server itself entered by mistake) is ignored with a warning instead of breaking every connection.
    /// </summary>
    internal static string? UsableProxy(string? proxyUrl, Uri server)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl)) return null;
        var p = proxyUrl!.Trim();
        if (!p.Contains("://")) p = "http://" + p;
        if (!Uri.TryCreate(p, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp)
        {
            Log.Warn($"Ignoring proxy '{proxyUrl}': only http://host:port proxies are supported. Connecting directly.");
            return null;
        }
        if (string.Equals(u.Host, server.Host, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"Ignoring proxy '{proxyUrl}': it is the Zentriq server itself, not a proxy. Connecting directly.");
            return null;
        }
        return u.GetLeftPart(UriPartial.Authority);
    }

    private static bool Validate(X509Certificate2? cert, SslPolicyErrors errors, IReadOnlyCollection<string> pins)
    {
        if (cert is null) return false;
        if (errors == SslPolicyErrors.None) return true;
        try
        {
            var spki = AgentProtocol.SpkiSha256(cert.RawData);
            if (pins.Contains(spki)) return true;
            Log.Warn($"Server certificate rejected ({errors}); its key pin {spki} is not one of the {pins.Count} pinned key(s).");
        }
        catch (FormatException ex) { Log.Warn("Server certificate could not be parsed: " + ex.Message); }
        return false;
    }

    public Task<EnrollResponse> EnrollAsync(string token, string csr, string hostname, string osName, string? smbiosUuid, int? checkinMinutes, int? inventoryMinutes, CancellationToken ct) =>
        PostJsonAsync<EnrollResponse>("api/agent/v1/enroll", new Dictionary<string, object?>
        {
            ["token"] = token, ["csr"] = csr, ["hostname"] = hostname, ["osName"] = osName, ["agentVersion"] = Program.Version, ["smbiosUuid"] = smbiosUuid,
            ["checkinMinutes"] = checkinMinutes, ["inventoryMinutes"] = inventoryMinutes,
        }, ct);

    public Task<HelloResponse> HelloAsync(string codeId, string nonce, CancellationToken ct) =>
        PostJsonAsync<HelloResponse>("api/agent/v1/enroll/hello", new Dictionary<string, object?> { ["codeId"] = codeId, ["nonce"] = nonce }, ct);

    public Task<CheckinResponse> CheckinAsync(string hostname, string osName, string? lastError, DateTime? offlineSince, bool? uninstallProtected, CancellationToken ct) =>
        PostJsonAsync<CheckinResponse>("api/agent/v1/checkin", new Dictionary<string, object?>
        {
            ["agentVersion"] = Program.Version, ["hostname"] = hostname, ["osName"] = osName, ["lastError"] = lastError,
            ["capabilities"] = new[] { "service-control", "software-uninstall", "self-update", "remote-support", "schedule", "uninstall-protection", "presence" },
            ["offlineSince"] = offlineSince?.ToString("o"), ["uninstallProtected"] = uninstallProtected,
        }, ct);

    /// <summary>The live connection (held open by the server for about 25 seconds; see <see cref="Presence"/>).</summary>
    public Task<PresenceResponse> PresenceAsync(string? uninstallCodeHash, CancellationToken ct) =>
        PostJsonAsync<PresenceResponse>("api/agent/v1/presence", uninstallCodeHash is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["uninstallCodeHash"] = uninstallCodeHash }, ct);

    /// <summary>The agent is stopping: the server shows it offline at once.</summary>
    public async Task LeaveAsync()
    {
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(new Uri(_base, "api/agent/v1/presence/leave"), content).ConfigureAwait(false);
        await EnsureAsync(resp).ConfigureAwait(false);
    }

    public async Task ReportActionAsync(string actionId, bool ok, string? state, string message, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["ok"] = ok, ["state"] = state, ["message"] = message };
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(new Uri(_base, "api/agent/v1/actions/" + Uri.EscapeDataString(actionId) + "/result"), content, ct).ConfigureAwait(false);
        await EnsureAsync(resp).ConfigureAwait(false);
    }

    /// <summary>Downloads the agent package served by this server (agent port, client certificate).</summary>
    public async Task DownloadPackageAsync(string file, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(new Uri(_base, "api/agent/v1/package"), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureAsync(resp).ConfigureAwait(false);
        using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var dst = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, 81920, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads the Zentriq Remote Support agent installer for this server (agent port, client certificate).</summary>
    public async Task DownloadRemoteAgentAsync(string file, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(new Uri(_base, "api/agent/v1/remote-agent"), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureAsync(resp).ConfigureAwait(false);
        using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var dst = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, 81920, ct).ConfigureAwait(false);
    }

    public Task<RenewResponse> RenewAsync(string csr, CancellationToken ct) =>
        PostJsonAsync<RenewResponse>("api/agent/v1/renew", new Dictionary<string, object?> { ["csr"] = csr }, ct);

    public async Task UploadInventoryAsync(byte[] gzipJson, CancellationToken ct)
    {
        using var content = new ByteArrayContent(gzipJson);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        using var resp = await _http.PostAsync(new Uri(_base, "api/agent/v1/inventory"), content, ct).ConfigureAwait(false);
        await EnsureAsync(resp).ConfigureAwait(false);
    }

    private async Task<T> PostJsonAsync<T>(string path, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(new Uri(_base, path), content, ct).ConfigureAwait(false);
        await EnsureAsync(resp).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json) ?? throw new ServerException(resp.StatusCode, "Empty response from server.");
    }

    private static async Task EnsureAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        string title = resp.ReasonPhrase ?? "error";
        try
        {
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString() ?? title;
        }
        catch (JsonException) { }
        throw new ServerException(resp.StatusCode, $"Server returned {(int)resp.StatusCode}: {title}");
    }

    public static byte[] Compress(InventoryDocument doc)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, doc);
        return ms.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
