using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Zentriq.Infrastructure.Cloud;

namespace Zentriq.Infrastructure.Backup;

internal static class BackupHttp
{
    public const int ChunkSize = 16 * 1024 * 1024;

    public static HttpClient Create() => new(new HttpClientHandler
    {
        UseProxy = true,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        AllowAutoRedirect = false,
    }) { Timeout = TimeSpan.FromMinutes(30) };

    public static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> SendAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct, bool keepResponse = false)
    {
        var resp = await http.SendAsync(req, keepResponse ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
        var body = keepResponse && resp.IsSuccessStatusCode ? "" : await resp.Content.ReadAsStringAsync(ct);
        return (resp.StatusCode, body, resp);
    }

    /// <summary>Reads the next chunk of a file (up to <paramref name="buffer"/>'s length).</summary>
    public static async Task<int> ReadChunkAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public static string XmlError(string body)
    {
        try
        {
            var x = XDocument.Parse(body).Root;
            var code = x?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value;
            var msg = x?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value?.Split('\n')[0];
            return string.Join(": ", new[] { code, msg }.Where(v => !string.IsNullOrWhiteSpace(v)));
        }
        catch (System.Xml.XmlException) { return body.Length > 200 ? body[..200] : body; }
    }
}

/// <summary>Azure Blob Storage: block blobs in a container, signed with the storage account key (Shared Key) or a SAS token.</summary>
public sealed class AzureBlobTarget(BackupTargetSettings s, string secret, HttpClient? http = null) : IBackupTarget
{
    public const string Version = "2021-08-06";
    private readonly HttpClient _http = http ?? BackupHttp.Create();
    private readonly string _account = s.User ?? "";
    private readonly string _container = s.Bucket ?? "";
    private readonly bool _sas = s.AuthMode == "sas";
    private readonly string _base = string.IsNullOrWhiteSpace(s.Endpoint) ? $"https://{s.User}.blob.core.windows.net" : s.Endpoint!.TrimEnd('/');

    public string Where => $"{_base}/{_container}/{BackupTargets.Join(s.Path, "")}".TrimEnd('/');
    /// <summary>Block size (tests use small blocks).</summary>
    internal int Chunk { get; init; } = BackupHttp.ChunkSize;

    private string Blob(string name) => BackupTargets.Join(s.Path, name);
    private static string EscapePath(string p) => string.Join('/', p.Split('/').Select(Uri.EscapeDataString));

    internal HttpRequestMessage Request(HttpMethod method, string? blob, IList<(string Name, string Value)> query, byte[]? body, DateTime utc)
    {
        var path = $"/{_container}" + (blob is null ? "" : "/" + EscapePath(blob));
        var q = string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));
        if (_sas) q = string.Join("&", new[] { q, secret.Trim().TrimStart('?') }.Where(x => x.Length > 0));
        var url = _base + path + (q.Length > 0 ? "?" + q : "");
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = new ByteArrayContent(body);
        var ms = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["x-ms-date"] = utc.ToString("R", CultureInfo.InvariantCulture), ["x-ms-version"] = Version };
        if (method == HttpMethod.Put && query.All(p => p.Name != "comp")) ms["x-ms-blob-type"] = "BlockBlob";
        foreach (var h in ms) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (!_sas)
        {
            var length = body is { Length: > 0 } ? body.Length.ToString(CultureInfo.InvariantCulture) : "";
            var resource = $"/{_account}{new Uri(url).AbsolutePath}" + string.Concat(query.GroupBy(p => p.Name.ToLowerInvariant())
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"\n{g.Key}:{string.Join(",", g.Select(x => x.Value).Order(StringComparer.Ordinal))}"));
            var toSign = $"{method.Method}\n\n\n{length}\n\n\n\n\n\n\n\n\n" + string.Concat(ms.Select(h => $"{h.Key}:{h.Value}\n")) + resource;
            byte[] key;
            try { key = Convert.FromBase64String(secret.Trim()); }
            catch (FormatException) { throw new BackupTargetException("The storage account key is not valid (copy key1 or key2 from the storage account's Access keys)."); }
            req.Headers.TryAddWithoutValidation("Authorization", $"SharedKey {_account}:{Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(toSign)))}");
        }
        return req;
    }

    private async Task<(HttpStatusCode, string, HttpResponseMessage)> Send(HttpMethod method, string? blob, IList<(string, string)> query, byte[]? body, CancellationToken ct, bool keep = false)
    {
        using var req = Request(method, blob, query, body, DateTime.UtcNow);
        var r = await BackupHttp.SendAsync(_http, req, ct, keep);
        if ((int)r.Status >= 300)
        {
            var detail = BackupHttp.XmlError(r.Body);
            r.Response.Dispose();
            var why = r.Status switch
            {
                HttpStatusCode.Forbidden => $"access denied — check the {(_sas ? "SAS token (it needs read, write, delete and list permission and must not have expired)" : "account key")}",
                HttpStatusCode.NotFound when detail.Contains("ContainerNotFound") => $"the container '{_container}' was not found",
                _ => detail.Length > 0 ? detail : r.Status.ToString(),
            };
            throw new BackupHttpException((int)r.Status, $"Azure Blob Storage: {why}");
        }
        return r;
    }

    public async Task UploadAsync(string localFile, string name, CancellationToken ct)
    {
        await using var src = File.OpenRead(localFile);
        var buffer = new byte[Math.Min(Chunk, Math.Max(1, (int)Math.Min(src.Length, int.MaxValue)))];
        var ids = new List<string>();
        int n;
        while ((n = await BackupHttp.ReadChunkAsync(src, buffer, ct)) > 0)
        {
            var id = Convert.ToBase64String(Encoding.ASCII.GetBytes($"zentriq-block-{ids.Count:D6}"));
            var chunk = buffer.AsSpan(0, n).ToArray();
            await BackupTargets.RetryAsync(async () => { (await Send(HttpMethod.Put, Blob(name), [("comp", "block"), ("blockid", id)], chunk, ct)).Item3.Dispose(); return true; }, ct);
            ids.Add(id);
        }
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>" + string.Concat(ids.Select(i => $"<Latest>{i}</Latest>")) + "</BlockList>";
        await BackupTargets.RetryAsync(async () => { (await Send(HttpMethod.Put, Blob(name), [("comp", "blocklist")], Encoding.UTF8.GetBytes(xml), ct)).Item3.Dispose(); return true; }, ct);
    }

    public async Task<List<RemoteBackup>> ListAsync(CancellationToken ct)
    {
        var list = new List<RemoteBackup>();
        var prefix = BackupTargets.Join(s.Path, "zentriq-");
        string? marker = null;
        do
        {
            var q = new List<(string, string)> { ("restype", "container"), ("comp", "list"), ("prefix", prefix) };
            if (marker is not null) q.Add(("marker", marker));
            var (_, body, resp) = await Send(HttpMethod.Get, null, q, null, ct);
            resp.Dispose();
            var x = XDocument.Parse(body);
            foreach (var b in x.Descendants("Blob"))
            {
                var full = b.Element("Name")?.Value ?? "";
                var n = full[(full.LastIndexOf('/') + 1)..];
                if (!BackupTargets.IsBackupName(n)) continue;
                var p = b.Element("Properties");
                long.TryParse(p?.Element("Content-Length")?.Value, out var size);
                list.Add(new RemoteBackup(n, size, DateTimeOffset.TryParse(p?.Element("Last-Modified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m : null));
            }
            marker = x.Root?.Element("NextMarker")?.Value is { Length: > 0 } nm ? nm : null;
        } while (marker is not null);
        return list;
    }

    public async Task DeleteAsync(string name, CancellationToken ct) => (await Send(HttpMethod.Delete, Blob(name), [], null, ct)).Item3.Dispose();

    public async Task DownloadAsync(string name, string localFile, CancellationToken ct)
    {
        var (_, _, resp) = await Send(HttpMethod.Get, Blob(name), [], null, ct, keep: true);
        using (resp)
        {
            await using var dst = File.Create(localFile);
            await resp.Content.CopyToAsync(dst, ct);
        }
    }

    public void Dispose() { if (http is null) _http.Dispose(); }
}

/// <summary>Amazon S3 or an S3-compatible service (Wasabi, MinIO, Backblaze B2, Cloudflare R2 …), signed with Signature Version 4.</summary>
public sealed class S3Target(BackupTargetSettings s, string secretKey, HttpClient? http = null, Func<DateTime>? clock = null) : IBackupTarget
{
    public const long MultipartAbove = 64L * 1024 * 1024;
    private readonly HttpClient _http = http ?? BackupHttp.Create();
    private readonly string _bucket = s.Bucket ?? "";
    private readonly string _region = string.IsNullOrWhiteSpace(s.Region) ? "us-east-1" : s.Region!.Trim();
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    public string Where => $"s3://{_bucket}/{BackupTargets.Join(s.Path, "")}".TrimEnd('/') + (string.IsNullOrWhiteSpace(s.Endpoint) ? "" : $" ({s.Endpoint})");
    /// <summary>Files above this size are sent in parts of at least <see cref="MinPart"/> bytes (tests use small values).</summary>
    internal long MultipartThreshold { get; init; } = MultipartAbove;
    internal int MinPart { get; init; } = BackupHttp.ChunkSize;

    private string Key(string name) => BackupTargets.Join(s.Path, name);

    /// <summary>URL, host and canonical path of an object (or of the bucket when <paramref name="key"/> is null).</summary>
    internal (string Url, string Host, string Path) Locate(string? key, string canonicalQuery)
    {
        var enc = key is null ? "" : AwsSigV4.Encode(key, path: true);
        string scheme, host, path;
        if (string.IsNullOrWhiteSpace(s.Endpoint))
        {
            (scheme, host, path) = ("https", $"{_bucket}.s3.{_region}.amazonaws.com", "/" + enc);
        }
        else
        {
            var u = new Uri(s.Endpoint!.TrimEnd('/'));
            scheme = u.Scheme;
            if (s.PathStyle) (host, path) = (u.Authority, $"/{_bucket}" + (key is null ? "" : "/" + enc));
            else (host, path) = ($"{_bucket}.{u.Authority}", "/" + enc);
        }
        return ($"{scheme}://{host}{path}{(canonicalQuery.Length > 0 ? "?" + canonicalQuery : "")}", host, path);
    }

    internal HttpRequestMessage Request(HttpMethod method, string? key, IEnumerable<KeyValuePair<string, string>> query, byte[]? body, bool unsignedPayload)
    {
        var cq = AwsSigV4.CanonicalQuery(query);
        var (url, host, path) = Locate(key, cq);
        var utc = _clock();
        var hash = unsignedPayload ? "UNSIGNED-PAYLOAD" : AwsSigV4.Hex(SHA256.HashData(body ?? Array.Empty<byte>()));
        var headers = new Dictionary<string, string>
        {
            ["host"] = host,
            ["x-amz-date"] = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
            ["x-amz-content-sha256"] = hash,
        };
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = new ByteArrayContent(body);
        req.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", hash);
        req.Headers.TryAddWithoutValidation("Authorization", AwsSigV4.Authorization(method.Method, path, cq, headers, hash, s.User ?? "", secretKey, _region, "s3", utc));
        return req;
    }

    private async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> Send(HttpMethod method, string? key, IEnumerable<KeyValuePair<string, string>> query, byte[]? body, CancellationToken ct, bool unsignedPayload = false, bool keep = false)
    {
        using var req = Request(method, key, query, body, unsignedPayload);
        var r = await BackupHttp.SendAsync(_http, req, ct, keep);
        if ((int)r.Status >= 300 || (!keep && r.Body.Contains("<Error>", StringComparison.Ordinal)))
        {
            r.Response.Dispose();
            var detail = BackupHttp.XmlError(r.Body);
            var why = detail switch
            {
                var d when d.StartsWith("InvalidAccessKeyId", StringComparison.Ordinal) => "the access key ID is not known",
                var d when d.StartsWith("SignatureDoesNotMatch", StringComparison.Ordinal) => "the secret access key is wrong",
                var d when d.StartsWith("NoSuchBucket", StringComparison.Ordinal) => $"the bucket '{_bucket}' does not exist",
                var d when d.StartsWith("AuthorizationHeaderMalformed", StringComparison.Ordinal) || d.StartsWith("PermanentRedirect", StringComparison.Ordinal) => $"the bucket is in another region than {_region}",
                var d when d.StartsWith("AccessDenied", StringComparison.Ordinal) => "access denied — the key needs s3:PutObject, s3:GetObject, s3:ListBucket and s3:DeleteObject on the bucket",
                "" => r.Status.ToString(),
                var d => d,
            };
            throw new BackupHttpException((int)r.Status == 200 ? 500 : (int)r.Status, $"S3: {why}");
        }
        return r;
    }

    private static KeyValuePair<string, string> Q(string k, string v) => new(k, v);

    public async Task UploadAsync(string localFile, string name, CancellationToken ct)
    {
        var size = new FileInfo(localFile).Length;
        if (size <= MultipartThreshold)
        {
            var body = await File.ReadAllBytesAsync(localFile, ct);
            await BackupTargets.RetryAsync(async () => { (await Send(HttpMethod.Put, Key(name), [], body, ct, unsignedPayload: true)).Response.Dispose(); return true; }, ct);
            return;
        }
        // Parts of 16 MB or more (at most 10,000 parts).
        var partSize = (int)Math.Max(MinPart, size / 9000 + 1);
        var (_, initBody, initResp) = await Send(HttpMethod.Post, Key(name), [Q("uploads", "")], [], ct);
        initResp.Dispose();
        var uploadId = XDocument.Parse(initBody).Descendants().FirstOrDefault(e => e.Name.LocalName == "UploadId")?.Value
                       ?? throw new BackupTargetException("S3 did not start the multipart upload.");
        try
        {
            var etags = new List<string>();
            await using var src = File.OpenRead(localFile);
            var buffer = new byte[partSize];
            int n;
            while ((n = await BackupHttp.ReadChunkAsync(src, buffer, ct)) > 0)
            {
                var part = etags.Count + 1;
                var chunk = buffer.AsSpan(0, n).ToArray();
                var etag = await BackupTargets.RetryAsync(async () =>
                {
                    var (_, _, resp) = await Send(HttpMethod.Put, Key(name), [Q("partNumber", part.ToString(CultureInfo.InvariantCulture)), Q("uploadId", uploadId)], chunk, ct, unsignedPayload: true);
                    using (resp) return resp.Headers.ETag?.Tag ?? (resp.Headers.TryGetValues("ETag", out var v) ? v.First() : throw new BackupTargetException("S3 returned no ETag for a part."));
                }, ct);
                etags.Add(etag);
            }
            var xml = "<CompleteMultipartUpload>" + string.Concat(etags.Select((e, i) => $"<Part><PartNumber>{i + 1}</PartNumber><ETag>{System.Security.SecurityElement.Escape(e)}</ETag></Part>")) + "</CompleteMultipartUpload>";
            (await Send(HttpMethod.Post, Key(name), [Q("uploadId", uploadId)], Encoding.UTF8.GetBytes(xml), ct)).Response.Dispose();
        }
        catch
        {
            try { (await Send(HttpMethod.Delete, Key(name), [Q("uploadId", uploadId)], null, CancellationToken.None)).Response.Dispose(); } catch (Exception) { }
            throw;
        }
    }

    public async Task<List<RemoteBackup>> ListAsync(CancellationToken ct)
    {
        var list = new List<RemoteBackup>();
        string? token = null;
        do
        {
            var q = new List<KeyValuePair<string, string>> { Q("list-type", "2"), Q("prefix", Key("zentriq-")) };
            if (token is not null) q.Add(Q("continuation-token", token));
            var (_, body, resp) = await Send(HttpMethod.Get, null, q, null, ct);
            resp.Dispose();
            var x = XDocument.Parse(body);
            foreach (var c in x.Descendants().Where(e => e.Name.LocalName == "Contents"))
            {
                string? V(string n) => c.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value;
                var full = V("Key") ?? "";
                var n = full[(full.LastIndexOf('/') + 1)..];
                if (!BackupTargets.IsBackupName(n)) continue;
                long.TryParse(V("Size"), out var size);
                list.Add(new RemoteBackup(n, size, DateTimeOffset.TryParse(V("LastModified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m : null));
            }
            token = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "IsTruncated")?.Value == "true"
                ? x.Descendants().FirstOrDefault(e => e.Name.LocalName == "NextContinuationToken")?.Value : null;
        } while (token is not null);
        return list;
    }

    public async Task DeleteAsync(string name, CancellationToken ct) => (await Send(HttpMethod.Delete, Key(name), [], null, ct)).Response.Dispose();

    public async Task DownloadAsync(string name, string localFile, CancellationToken ct)
    {
        var (_, _, resp) = await Send(HttpMethod.Get, Key(name), [], null, ct, keep: true);
        using (resp)
        {
            await using var dst = File.Create(localFile);
            await resp.Content.CopyToAsync(dst, ct);
        }
    }

    public void Dispose() { if (http is null) _http.Dispose(); }
}

/// <summary>Google Cloud Storage with a service account key (resumable uploads in 16 MB pieces).</summary>
public sealed class GcsTarget(BackupTargetSettings s, string keyJson, HttpClient? http = null, string? apiBase = null, string? tokenUrl = null) : IBackupTarget
{
    public const string Scope = "https://www.googleapis.com/auth/devstorage.read_write";
    private readonly HttpClient _http = http ?? BackupHttp.Create();
    private readonly string _api = (apiBase ?? "https://storage.googleapis.com").TrimEnd('/');
    private readonly string _bucket = s.Bucket ?? "";
    private string? _token;
    private DateTimeOffset _tokenUntil;

    public string Where => $"gs://{_bucket}/{BackupTargets.Join(s.Path, "")}".TrimEnd('/');
    /// <summary>Upload piece size, a multiple of 256 KiB (tests use small pieces).</summary>
    internal int Chunk { get; init; } = BackupHttp.ChunkSize;
    private string Object(string name) => BackupTargets.Join(s.Path, name);

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenUntil) return _token;
        var key = GcpCollector.ParseKey(keyJson) ?? throw new BackupTargetException("The service account JSON key is not valid.");
        var uri = tokenUrl ?? key.TokenUri ?? "https://oauth2.googleapis.com/token";
        string assertion;
        try { assertion = GcpCollector.Assertion(key, uri, DateTimeOffset.UtcNow, Scope); }
        catch (CloudException ex) { throw new BackupTargetException(ex.Message, ex); }
        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = assertion }),
        };
        var (status, body, resp) = await BackupHttp.SendAsync(_http, req, ct);
        resp.Dispose();
        if (status != HttpStatusCode.OK)
            throw new BackupHttpException((int)status, body.Contains("invalid_grant") ? "Google sign-in failed: the key was not accepted (deleted or disabled key, or the server clock is wrong)." : "Google sign-in failed: " + Error(body));
        using var d = JsonDocument.Parse(body);
        _token = d.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        _tokenUntil = DateTimeOffset.UtcNow.AddMinutes(45);
        return _token ?? throw new BackupTargetException("Google sign-in returned no access token.");
    }

    private static string Error(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            var r = d.RootElement;
            if (r.TryGetProperty("error", out var e))
                return e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : (r.TryGetProperty("error_description", out var ed) ? ed.GetString() ?? "" : e.ToString());
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] : body;
    }

    private async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> Send(HttpRequestMessage req, CancellationToken ct, bool keep = false, params HttpStatusCode[] ok)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        var r = await BackupHttp.SendAsync(_http, req, ct, keep);
        if ((int)r.Status >= 300 && !ok.Contains(r.Status))
        {
            r.Response.Dispose();
            var why = r.Status switch
            {
                HttpStatusCode.Forbidden => "access denied — give the service account the Storage Object Admin role on the bucket",
                HttpStatusCode.NotFound when r.Body.Contains("bucket", StringComparison.OrdinalIgnoreCase) => $"the bucket '{_bucket}' was not found",
                _ => Error(r.Body),
            };
            throw new BackupHttpException((int)r.Status, $"Google Cloud Storage: {why}");
        }
        return r;
    }

    public async Task UploadAsync(string localFile, string name, CancellationToken ct)
    {
        await using var src = File.OpenRead(localFile);
        var total = src.Length;
        using var start = new HttpRequestMessage(HttpMethod.Post, $"{_api}/upload/storage/v1/b/{Uri.EscapeDataString(_bucket)}/o?uploadType=resumable&name={Uri.EscapeDataString(Object(name))}")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        start.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
        start.Headers.TryAddWithoutValidation("X-Upload-Content-Length", total.ToString(CultureInfo.InvariantCulture));
        var (_, _, sresp) = await Send(start, ct);
        var session = sresp.Headers.Location ?? throw new BackupTargetException("Google Cloud Storage did not start the upload.");
        sresp.Dispose();
        var buffer = new byte[(int)Math.Min(Chunk, Math.Max(1, total))];
        long offset = 0;
        while (offset < total)
        {
            var n = await BackupHttp.ReadChunkAsync(src, buffer, ct);
            if (n == 0) throw new BackupTargetException("The backup file changed while it was uploaded.");
            var chunk = buffer.AsSpan(0, n).ToArray();
            var from = offset;
            await BackupTargets.RetryAsync(async () =>
            {
                using var put = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(chunk) };
                put.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, from + n - 1, total);
                var (_, _, presp) = await Send(put, ct, false, (HttpStatusCode)308);
                presp.Dispose();
                return true;
            }, ct);
            offset += n;
        }
    }

    public async Task<List<RemoteBackup>> ListAsync(CancellationToken ct)
    {
        var list = new List<RemoteBackup>();
        string? page = null;
        do
        {
            var url = $"{_api}/storage/v1/b/{Uri.EscapeDataString(_bucket)}/o?prefix={Uri.EscapeDataString(Object("zentriq-"))}&fields=items(name,size,updated),nextPageToken"
                      + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var (_, body, resp) = await Send(req, ct);
            resp.Dispose();
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("items", out var items))
                foreach (var it in items.EnumerateArray())
                {
                    var full = it.GetProperty("name").GetString() ?? "";
                    var n = full[(full.LastIndexOf('/') + 1)..];
                    if (!BackupTargets.IsBackupName(n)) continue;
                    long.TryParse(it.TryGetProperty("size", out var sz) ? sz.GetString() : null, out var size);
                    DateTimeOffset? m = it.TryGetProperty("updated", out var up) && DateTimeOffset.TryParse(up.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var mm) ? mm : null;
                    list.Add(new RemoteBackup(n, size, m));
                }
            page = d.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
        } while (page is not null);
        return list;
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_api}/storage/v1/b/{Uri.EscapeDataString(_bucket)}/o/{Uri.EscapeDataString(Object(name))}");
        (await Send(req, ct)).Response.Dispose();
    }

    public async Task DownloadAsync(string name, string localFile, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_api}/storage/v1/b/{Uri.EscapeDataString(_bucket)}/o/{Uri.EscapeDataString(Object(name))}?alt=media");
        var (_, _, resp) = await Send(req, ct, keep: true);
        using (resp)
        {
            await using var dst = File.Create(localFile);
            await resp.Content.CopyToAsync(dst, ct);
        }
    }

    public void Dispose() { if (http is null) _http.Dispose(); }
}
