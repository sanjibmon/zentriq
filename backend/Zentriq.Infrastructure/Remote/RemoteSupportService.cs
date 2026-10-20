using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Remote;

/// <summary>Consent policy applied to the Zentriq device group.</summary>
public sealed class RemoteSettings
{
    /// <summary>Ask the person at the device to allow each remote desktop, terminal or file session.</summary>
    public bool Prompt { get; set; } = true;
    /// <summary>Notify the person at the device when a session starts.</summary>
    public bool Notify { get; set; } = true;
    /// <summary>Show a bar on the device's screen during remote desktop sessions (with a disconnect button).</summary>
    public bool PrivacyBar { get; set; } = true;
    /// <summary>Technicians may open a command window (terminal) on devices. Remote desktop and file transfer are always available.</summary>
    public bool AllowTerminal { get; set; } = true;
    /// <summary>Public DNS name (or address) that devices and users outside the network use to reach remote support.</summary>
    public string? InternetHost { get; set; }
    /// <summary>Port published on the router/firewall for <see cref="InternetHost"/> (forwarded to the remote support port).</summary>
    public int? InternetPort { get; set; }
}

public sealed record RemoteStatus(bool Installed, bool Running, string? Message, string? PublicUrl, int Port);

public sealed record SyncResult(int Nodes, int Connected, int Mapped);

/// <summary>
/// Zentriq Remote Support: the bridge between Zentriq and the bundled MeshCentral server (Apache-2.0) run by the
/// ZentriqRemote service. Keeps one device group for Zentriq, maps remote agents to assets, creates a remote support
/// account per technician on first use and signs technicians in with short-lived links (no separate password).
/// </summary>
public sealed partial class RemoteSupportService(NpgsqlDataSource ds, ZentriqOptions options, Func<AppSecrets> secrets, ILogger<RemoteSupportService> logger)
{
    public const string GroupName = "Zentriq devices";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string? Key
    {
        get { try { return secrets().RemoteLoginKey; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException) { return null; } }
    }

    public bool Configured => options.Remote.Enabled && Key is { Length: 160 };
    public int Port => options.Remote.Port;
    public Uri LocalUrl => new($"https://127.0.0.1:{options.Remote.Port}/");
    public string PublicHost => string.IsNullOrWhiteSpace(options.Remote.PublicHost) ? options.Https.HostName : options.Remote.PublicHost.Trim();
    public string PublicUrl => $"https://{PublicHost}:{options.Remote.Port}/";

    /// <summary>
    /// The address technicians open: the configured public host if any, otherwise the host name (or IP address) the
    /// technician's browser used to reach the console. The server's own name may not resolve on the technician's PC.
    /// </summary>
    public string UrlFor(string? browserHost)
    {
        if (string.IsNullOrWhiteSpace(options.Remote.PublicHost) && !string.IsNullOrWhiteSpace(browserHost)
            && Uri.CheckHostName(browserHost) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            var h = Uri.CheckHostName(browserHost) == UriHostNameType.IPv6 && !browserHost.StartsWith('[') ? $"[{browserHost}]" : browserHost;
            return $"https://{h}:{options.Remote.Port}/";
        }
        return PublicUrl;
    }

    private static readonly HttpClient Loopback = new(new SocketsHttpHandler
    {
        // The remote support server runs on this machine; its certificate is its own self-signed one.
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<RemoteStatus> StatusAsync(CancellationToken ct)
    {
        if (!options.Remote.Enabled) return new RemoteStatus(false, false, "Remote support is not installed on this server. Run the latest Zentriq Setup (Repair) to add it.", null, options.Remote.Port);
        if (Key is not { Length: 160 }) return new RemoteStatus(true, false, "The remote support key is missing. Run Zentriq Setup again (Repair).", PublicUrl, options.Remote.Port);
        try
        {
            using var r = await Loopback.GetAsync(new Uri(LocalUrl, "health.ashx"), ct);
            return r.IsSuccessStatusCode
                ? new RemoteStatus(true, true, null, PublicUrl, options.Remote.Port)
                : new RemoteStatus(true, false, $"The remote support server answered HTTP {(int)r.StatusCode}.", PublicUrl, options.Remote.Port);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new RemoteStatus(true, false, "The ZentriqRemote service is not answering (it may be starting; check the service in services.msc).", PublicUrl, options.Remote.Port);
        }
    }

    public Task<MeshControlClient> ConnectAsync(CancellationToken ct)
    {
        if (!Configured) throw new MeshException("Remote support is not configured on this server.");
        return MeshControlClient.ConnectAsync(LocalUrl, options.Remote.ServiceUser, Key!, ct);
    }

    // ---- settings -----------------------------------------------------------------------------------

    public async Task<RemoteSettings> SettingsAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'remote.settings'");
        return await cmd.ExecuteScalarAsync(ct) is string s ? JsonSerializer.Deserialize<RemoteSettings>(s, Json) ?? new() : new();
    }

    public async Task SaveSettingsAsync(RemoteSettings s, CancellationToken ct)
    {
        await SetSettingAsync("remote.settings", JsonSerializer.Serialize(s, Json), ct);
        if (!Configured) return;
        await using var c = await ConnectAsync(ct);
        var mesh = await EnsureGroupAsync(c, s, ct);
        await c.SetGroupConsentAsync(mesh, MeshRights.Consent(s.Prompt, s.Notify, s.PrivacyBar), ct);
    }

    private async Task<string?> SettingAsync(string key, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value #>> '{}' FROM zentriq.system_settings WHERE key = @k");
        cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private async Task SetSettingAsync(string key, string json, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.system_settings (key, value) VALUES (@k, @v::jsonb)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("v", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The Zentriq device group (created on first use, consent applied when created).</summary>
    public async Task<string> EnsureGroupAsync(MeshControlClient c, RemoteSettings? settings, CancellationToken ct)
    {
        var stored = await SettingAsync("remote.mesh_id", ct);
        var groups = await c.GroupsAsync(ct);
        if (stored is not null && groups.ContainsKey(stored)) return stored;
        var existing = groups.FirstOrDefault(g => g.Value == GroupName).Key;
        if (existing is null)
        {
            var s = settings ?? await SettingsAsync(ct);
            existing = await c.CreateGroupAsync(GroupName, "Devices managed by Zentriq. Do not rename.", MeshRights.Consent(s.Prompt, s.Notify, s.PrivacyBar), ct);
            logger.LogInformation("Remote support device group created ({Mesh})", existing);
        }
        await SetSettingAsync("remote.mesh_id", JsonSerializer.Serialize(existing), ct);
        return existing;
    }

    // ---- devices ------------------------------------------------------------------------------------

    /// <summary>Reads the remote agents from the server and maps each to the Zentriq asset with the same host name.</summary>
    public async Task<SyncResult> SyncAsync(CancellationToken ct)
    {
        await using var c = await ConnectAsync(ct);
        var mesh = await EnsureGroupAsync(c, null, ct);
        var nodes = await c.NodesAsync(mesh, ct);
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var n in nodes)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.remote_nodes AS r (node_id, name, host, os, ip, connected, last_connected_at, synced_at, asset_id)
                VALUES (@id, @name, @host, @os, @ip, @conn, CASE WHEN @conn THEN now() END, now(),
                        (SELECT a.id FROM zentriq.assets a
                          WHERE a.status = 'active' AND (lower(a.hostname) = lower(@name) OR lower(split_part(a.fqdn, '.', 1)) = lower(@name))
                          ORDER BY a.last_seen_at DESC LIMIT 1))
                ON CONFLICT (node_id) DO UPDATE SET name = EXCLUDED.name, host = EXCLUDED.host, os = EXCLUDED.os, ip = COALESCE(EXCLUDED.ip, r.ip),
                       connected = EXCLUDED.connected, last_connected_at = CASE WHEN EXCLUDED.connected THEN now() ELSE r.last_connected_at END, synced_at = now(),
                       asset_id = CASE WHEN r.mapped_by = 'manual' THEN r.asset_id ELSE COALESCE(EXCLUDED.asset_id, r.asset_id) END
                """, conn, tx);
            cmd.Parameters.AddWithValue("id", n.Id);
            cmd.Parameters.AddWithValue("name", n.Name);
            cmd.Parameters.Add(new NpgsqlParameter("host", NpgsqlDbType.Text) { Value = (object?)n.Host ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("os", NpgsqlDbType.Text) { Value = (object?)n.Os ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Text) { Value = (object?)n.Ip ?? DBNull.Value });
            cmd.Parameters.AddWithValue("conn", n.Connected);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var del = new NpgsqlCommand("DELETE FROM zentriq.remote_nodes WHERE NOT (node_id = ANY(@ids))", conn, tx))
        {
            del.Parameters.AddWithValue("ids", nodes.Select(n => n.Id).ToArray());
            await del.ExecuteNonQueryAsync(ct);
        }
        int mapped;
        await using (var m = new NpgsqlCommand("SELECT count(*) FROM zentriq.remote_nodes WHERE asset_id IS NOT NULL", conn, tx))
            mapped = (int)(long)(await m.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return new SyncResult(nodes.Count, nodes.Count(n => n.Connected), mapped);
    }

    // ---- sessions -----------------------------------------------------------------------------------

    public static string MeshUserName(Guid userId) => "zq-" + userId.ToString("N");
    public static int ViewMode(string view) => view switch { "desktop" => 11, "terminal" => 12, "files" => 13, _ => 10 };

    /// <summary>
    /// A sign-in link for one technician that opens the device (or the full remote console when nodeId is null).
    /// The technician's remote support account is created or updated first; the link is valid for 2 minutes.
    /// </summary>
    public async Task<string> SessionUrlAsync(Guid userId, string displayName, bool fullRights, bool viewOnly, string? nodeId, string view, CancellationToken ct, string? browserHost = null, string? groupId = null)
    {
        await using var c = await ConnectAsync(ct);
        var mesh = await EnsureGroupAsync(c, null, ct);
        var name = MeshUserName(userId);
        var meshUser = "user//" + name;
        await c.EnsureUserAsync(name, displayName, null, ct);
        var settings = await SettingsAsync(ct);
        await c.GrantGroupAsync(groupId ?? mesh, meshUser, fullRights ? MeshRights.Full : MeshRights.Technician(settings.AllowTerminal) | (viewOnly ? MeshRights.ViewOnly : 0), ct);
        var url = UrlFor(browserHost) + "?login=" + Uri.EscapeDataString(MeshCookie.Login(meshUser, Key!));
        if (nodeId is not null)
        {
            var tail = nodeId.StartsWith("node//", StringComparison.Ordinal) ? nodeId["node//".Length..] : nodeId;
            url += "&gotonode=" + Uri.EscapeDataString(tail) + "&viewmode=" + ViewMode(view);
        }
        return url;
    }

    /// <summary>
    /// Makes sure the engine accepts sessions opened from <paramref name="browserHost"/> (it refuses browser origins it does
    /// not know). Server names and addresses are accepted already; a new host name (for example a DNS alias) is recorded
    /// and this waits until the remote support service has restarted with it (up to 90 seconds).
    /// </summary>
    public async Task EnsureOriginAsync(string sharedDir, string? browserHost, CancellationToken ct)
    {
        if (RemoteServerConfig.NormalizeHost(browserHost) is not { } h || !Directory.Exists(sharedDir)) return;
        var applied = Path.Combine(sharedDir, RemoteServerConfig.AppliedOriginsFile);
        bool Accepted() => RemoteServerConfig.ReadLines(applied).Contains(h, StringComparer.Ordinal);
        try
        {
            if (Accepted()) return;
            RemoteServerConfig.RememberOrigin(sharedDir, h);
            logger.LogInformation("Remote support: adding {Host} to the host names the engine accepts.", h);
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline && !Accepted()) await Task.Delay(2000, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Remote support: could not record host name {Host}: {Message}", h, ex.Message);
        }
    }

    /// <summary>
    /// An invitation page for installing the agent into the Zentriq device group (unattended access) on Windows, macOS or
    /// Linux, valid for <paramref name="hours"/> hours; uses the internet address when one is set.
    /// </summary>
    public async Task<string> InstallLinkAsync(int hours, string? browserHost, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        await using var c = await ConnectAsync(ct);
        var mesh = await EnsureGroupAsync(c, settings, ct);
        var cookie = await c.CreateInviteAsync(mesh, hours, flags: 2, ct); // install only (background service)
        return InviteBase(settings, browserHost) + "agentinvite?c=" + Uri.EscapeDataString(cookie);
    }

    // ---- agent installer ----------------------------------------------------------------------------

    private (byte[] Bytes, string Sha256, DateTimeOffset At)? _installer;
    private readonly SemaphoreSlim _installerLock = new(1, 1);

    /// <summary>The branded Windows (x64) remote agent installer for the Zentriq group (cached 30 minutes).</summary>
    public async Task<(byte[] Bytes, string Sha256)> AgentInstallerAsync(CancellationToken ct)
    {
        await _installerLock.WaitAsync(ct);
        try
        {
            if (_installer is { } i && i.At > DateTimeOffset.UtcNow.AddMinutes(-30)) return (i.Bytes, i.Sha256);
            string mesh;
            await using (var c = await ConnectAsync(ct)) mesh = await EnsureGroupAsync(c, null, ct);
            var meshTail = mesh.StartsWith("mesh//", StringComparison.Ordinal) ? mesh["mesh//".Length..] : mesh;
            using var r = await Loopback.GetAsync(new Uri(LocalUrl, $"meshagents?id=4&meshid={Uri.EscapeDataString(meshTail)}&installflags=0"), ct);
            if (!r.IsSuccessStatusCode) throw new MeshException($"The remote support server did not provide the agent installer (HTTP {(int)r.StatusCode}).");
            var bytes = await r.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 100_000 || bytes[0] != 'M' || bytes[1] != 'Z') throw new MeshException("The remote support server returned something that is not a Windows program.");
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            _installer = (bytes, sha, DateTimeOffset.UtcNow);
            return (bytes, sha);
        }
        finally { _installerLock.Release(); }
    }
}
