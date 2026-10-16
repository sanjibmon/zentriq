using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zentriq.Infrastructure.Remote;

/// <summary>
/// Login cookies understood by the MeshCentral server that powers Zentriq Remote Support (Apache-2.0).
/// Same format as MeshCentral's encodeCookie(): AES-256-GCM with the first 32 bytes of the 80-byte
/// LoginCookieEncryptionKey, laid out as iv(12) | tag(16) | ciphertext, base64 with '+'→'@' and '/'→'$'.
/// </summary>
public static class MeshCookie
{
    public static string NewKeyHex() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(80));

    public static string Encode(JsonObject payload, string keyHex, DateTimeOffset? now = null)
    {
        var key = Convert.FromHexString(keyHex);
        if (key.Length != 80) throw new ArgumentException("The remote support login key must be 80 bytes (160 hex characters).");
        payload["time"] = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var plain = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var iv = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key.AsSpan(0, 32), 16)) aes.Encrypt(iv, plain, cipher, tag);
        return Convert.ToBase64String([.. iv, .. tag, .. cipher]).Replace('+', '@').Replace('/', '$');
    }

    /// <summary>Decodes a cookie (tests and diagnostics). Returns null when it does not decrypt.</summary>
    public static JsonObject? Decode(string cookie, string keyHex)
    {
        try
        {
            var key = Convert.FromHexString(keyHex);
            var data = Convert.FromBase64String(cookie.Replace('@', '+').Replace('$', '/'));
            var plain = new byte[data.Length - 28];
            using var aes = new AesGcm(key.AsSpan(0, 32), 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
            return JsonNode.Parse(plain) as JsonObject;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or JsonException) { return null; }
    }

    /// <summary>Authenticates a control (management) connection as <paramref name="user"/>.</summary>
    public static string ControlAuth(string user, string keyHex) =>
        Encode(new JsonObject { ["userid"] = $"user//{user}", ["domainid"] = "" }, keyHex);

    /// <summary>A web sign-in token for one technician; valid for <paramref name="minutes"/> minutes.</summary>
    public static string Login(string meshUserId, string keyHex, int minutes = 2) =>
        Encode(new JsonObject { ["u"] = meshUserId, ["a"] = 3, ["expire"] = minutes }, keyHex);
}

/// <summary>A device known to the remote support server.</summary>
public sealed record MeshNode(string Id, string Name, string? Host, string? Os, bool Connected, string? Ip, int? AgentId, string? MeshId)
{
    /// <summary>The part after "node//" used in URLs (gotonode=).</summary>
    public string UrlId => Id.StartsWith("node//", StringComparison.Ordinal) ? Id["node//".Length..] : Id;
}

public sealed class MeshException(string message) : Exception(message);

/// <summary>
/// Minimal client for the MeshCentral control WebSocket (control.ashx), the same JSON protocol MeshCentral's own
/// meshctrl tool uses. Authenticates with a login-key cookie for the service account, so no password is stored.
/// </summary>
public sealed class MeshControlClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _reader;

    public static async Task<MeshControlClient> ConnectAsync(Uri baseUrl, string serviceUser, string keyHex, CancellationToken ct)
    {
        var c = new MeshControlClient();
        // The server is on this machine (loopback); its certificate is MeshCentral's own self-signed one.
        c._ws.Options.RemoteCertificateValidationCallback = (_, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || baseUrl.IsLoopback;
        c._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        var url = new UriBuilder(new Uri(baseUrl, "control.ashx?auth=" + Uri.EscapeDataString(MeshCookie.ControlAuth(serviceUser, keyHex))))
        {
            Scheme = baseUrl.Scheme == Uri.UriSchemeHttp ? "ws" : "wss",
        }.Uri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await c._ws.ConnectAsync(url, timeout.Token);
        c._reader = Task.Run(c.ReadLoopAsync);
        return c;
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) { Fail("The remote support server closed the connection."); return; }
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                JsonObject? msg;
                try { msg = JsonNode.Parse(ms.ToArray()) as JsonObject; } catch (JsonException) { continue; }
                if (msg is null) continue;
                if (msg["action"]?.GetValue<string>() == "close")
                {
                    Fail("The remote support server refused the management connection (" + (msg["cause"]?.ToString() ?? "no reason") + "). Check that the login key matches.");
                    return;
                }
                // Most replies echo "responseid"; a few (e.g. "meshes") echo "tag" instead.
                var rid = (msg["responseid"] as JsonValue)?.ToString() ?? (msg["tag"] as JsonValue)?.ToString();
                if (rid is not null && _pending.TryRemove(rid, out var tcs)) tcs.TrySetResult(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { Fail("Connection to the remote support server lost: " + ex.Message); }
    }

    private void Fail(string message)
    {
        foreach (var k in _pending.Keys) if (_pending.TryRemove(k, out var t)) t.TrySetException(new MeshException(message));
    }

    /// <summary>Sends one action and waits for the reply with the same responseid.</summary>
    public async Task<JsonObject> SendAsync(JsonObject action, CancellationToken ct, TimeSpan? timeout = null)
    {
        var id = Guid.NewGuid().ToString("N");
        action["responseid"] = id;
        action["tag"] = id;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await _ws.SendAsync(Encoding.UTF8.GetBytes(action.ToJsonString()), WebSocketMessageType.Text, true, ct);
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        await using (t.Token.Register(() => { if (_pending.TryRemove(id, out var x)) x.TrySetException(new MeshException($"No answer to '{action["action"]}' from the remote support server.")); }))
            return await tcs.Task;
    }

    private static string? Result(JsonObject r) => r["result"]?.ToString();
    private static bool Ok(JsonObject r) => Result(r) is null or "ok";

    /// <summary>Device groups visible to the service account: id → name.</summary>
    public async Task<Dictionary<string, string>> GroupsAsync(CancellationToken ct)
    {
        var r = await SendAsync(new JsonObject { ["action"] = "meshes" }, ct);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (r["meshes"] is JsonArray a)
            foreach (var m in a.OfType<JsonObject>())
                if (m["_id"]?.ToString() is { } id) d[id] = m["name"]?.ToString() ?? id;
        return d;
    }

    /// <summary>Creates a device group (agent-managed) with the given consent flags; returns its id ("mesh//…").</summary>
    public async Task<string> CreateGroupAsync(string name, string description, int consent, CancellationToken ct)
    {
        var r = await SendAsync(new JsonObject { ["action"] = "createmesh", ["meshname"] = name, ["meshtype"] = 2, ["desc"] = description, ["consent"] = consent }, ct);
        if (!Ok(r) || r["meshid"]?.ToString() is not { } id) throw new MeshException("Could not create the device group: " + Result(r));
        return id;
    }

    public async Task SetGroupConsentAsync(string meshId, int consent, CancellationToken ct)
    {
        var r = await SendAsync(new JsonObject { ["action"] = "editmesh", ["meshid"] = meshId, ["consent"] = consent }, ct);
        if (!Ok(r)) throw new MeshException("Could not update the device group: " + Result(r));
    }

    /// <summary>Creates an account with a random password (it is never used: sign-in is by Zentriq token). Existing accounts are kept.</summary>
    public async Task EnsureUserAsync(string username, string realName, string? email, CancellationToken ct)
    {
        var op = new JsonObject { ["action"] = "adduser", ["username"] = username, ["randomPassword"] = true, ["realname"] = realName };
        if (!string.IsNullOrWhiteSpace(email)) { op["email"] = email; op["emailVerified"] = true; }
        var r = await SendAsync(op, ct);
        var res = Result(r);
        if (res is null or "ok") return;
        if (res.Contains("exist", StringComparison.OrdinalIgnoreCase)) return;
        throw new MeshException($"Could not create the remote support account {username}: {res}");
    }

    public async Task GrantGroupAsync(string meshId, string userId, uint rights, CancellationToken ct)
    {
        var r = await SendAsync(new JsonObject { ["action"] = "addmeshuser", ["meshid"] = meshId, ["userids"] = new JsonArray(userId), ["meshadmin"] = rights }, ct);
        // addmeshuser answers with a summary text; "failed" counts > 0 means it did not apply.
        if (r["failed"] is JsonValue f && f.TryGetValue<int>(out var failed) && failed > 0) throw new MeshException("Could not grant access to the device group: " + Result(r));
    }

    public async Task<List<MeshNode>> NodesAsync(string? meshId, CancellationToken ct)
    {
        var op = new JsonObject { ["action"] = "nodes" };
        if (meshId is not null) op["meshid"] = meshId;
        var r = await SendAsync(op, ct);
        var list = new List<MeshNode>();
        if (r["nodes"] is not JsonObject groups) return list;
        foreach (var (gid, arr) in groups)
            if (arr is JsonArray nodes)
                foreach (var n in nodes.OfType<JsonObject>())
                {
                    if (n["_id"]?.ToString() is not { } id) continue;
                    var conn = n["conn"] is JsonValue cv && cv.TryGetValue<int>(out var ci) ? ci : 0;
                    int? agentId = n["agent"] is JsonObject ag && ag["id"] is JsonValue av && av.TryGetValue<int>(out var ai) ? ai : null;
                    list.Add(new MeshNode(id, n["name"]?.ToString() ?? id, n["host"]?.ToString(), n["osdesc"]?.ToString(), (conn & 1) != 0, n["ip"]?.ToString(), agentId, n["meshid"]?.ToString() ?? gid));
                }
        return list;
    }

    public async Task RemoveNodeAsync(string nodeId, CancellationToken ct)
    {
        var r = await SendAsync(new JsonObject { ["action"] = "removedevices", ["nodeids"] = new JsonArray(nodeId) }, ct);
        if (!Ok(r)) throw new MeshException("Could not remove the device: " + Result(r));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", t.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        _cts.Cancel();
        if (_reader is not null) try { await _reader; } catch { /* closing */ }
        _ws.Dispose();
        _cts.Dispose();
    }
}

/// <summary>MeshCentral device-group rights and consent flags used by Zentriq.</summary>
public static class MeshRights
{
    public const uint RemoteControl = 8;
    public const uint ViewOnly = 256;
    public const uint Full = 0xFFFFFFFF;

    /// <summary>Consent flags: 1/2/4 notify (desktop, terminal, files), 8/16/32 prompt, 64 toolbar (privacy bar).</summary>
    public static int Consent(bool prompt, bool notify, bool privacyBar) =>
        (notify ? 1 | 2 | 4 : 0) | (prompt ? 8 | 16 | 32 : 0) | (privacyBar ? 64 : 0);
}
