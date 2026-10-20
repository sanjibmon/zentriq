using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Remote;

/// <summary>An invitation for people outside the office needs a public address that has been checked.</summary>
public sealed class InternetRequiredException(string? detail) : Exception(
    "Invitations for people outside your network need a public address (public IP or domain name) that reaches this server over HTTPS. " +
    "Set and check it in Remote Support › Settings › Internet access." + (string.IsNullOrEmpty(detail) ? "" : " Last check: " + detail));

/// <summary>A quick support session as listed in the console.</summary>
public sealed record QuickSession(
    long Id, string Code, string InviteUrl, string? Customer, string? Email, string? Note, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, string Status, string? NodeId, string? DeviceName, string? DeviceOs, string? DeviceIp, bool Online,
    DateTimeOffset? ConnectedAt, DateTimeOffset? LastSeenAt, DateTimeOffset? EndedAt, string? EndedBy, string? EndReason);

/// <summary>
/// Quick support: on-demand sessions for a person anywhere (in the office or over the internet), in the way hosted
/// support tools work. The technician creates a session and sends its link; the person opens it, runs the small
/// Zentriq Remote Support app (it runs only while open and installs nothing) and approves each request (screen,
/// files, terminal). Every session has its own device group with consent always on, removed when the session ends.
/// </summary>
public sealed partial class RemoteSupportService
{
    public const string QuickGroupPrefix = "Quick support ";
    /// <summary>A connected session whose device has been offline this long is ended.</summary>
    public static readonly TimeSpan QuickOfflineLimit = TimeSpan.FromMinutes(30);
    /// <summary>No session lasts longer than this.</summary>
    public static readonly TimeSpan QuickMaxDuration = TimeSpan.FromHours(12);

    /// <summary>A 9-digit reference, shown as "123 456 789".</summary>
    public static string NewQuickCode() => RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string FormatCode(string code) => code.Length == 9 ? $"{code[..3]} {code[3..6]} {code[6..]}" : code;

    /// <summary>
    /// The address invitation links use: the internet address (HTTPS) for people anywhere, or — for someone on the office
    /// network (<paramref name="lan"/>) — the address the technician uses.
    /// </summary>
    public string InviteBase(RemoteSettings s, string? browserHost, bool lan = false) =>
        !lan && RemoteServerConfig.NormalizeHost(s.InternetHost) is { } h ? $"https://{h}:{(s.InternetPort is > 0 and < 65536 ? s.InternetPort : options.Remote.Port)}/" : UrlFor(browserHost);

    private const string InternetCheckKey = "remote.internet_check";

    /// <summary>The last check of the internet address (null when none is set).</summary>
    public async Task<InternetCheckResult?> InternetCheckAsync(CancellationToken ct) =>
        await SettingAsync(InternetCheckKey, ct) is { } json ? JsonSerializer.Deserialize<InternetCheckResult>(json, Json) : null;

    /// <summary>Checks <paramref name="host"/>:<paramref name="port"/> from this server (see <see cref="InternetCheck"/>).</summary>
    public Task<InternetCheckResult> CheckInternetAsync(string host, int port, bool confirmedFromOutside, CancellationToken ct) =>
        InternetCheck.RunAsync(host, port, options.Remote.Port, ct, confirmedFromOutside);

    /// <summary>Stores the internet address with its check result (or removes both).</summary>
    public async Task SaveInternetAsync(InternetCheckResult? check, CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        s.InternetHost = check?.Host;
        s.InternetPort = check?.Port;
        await SetSettingAsync("remote.settings", JsonSerializer.Serialize(s, Json), ct);
        if (check is null)
        {
            await using var del = ds.CreateCommand("DELETE FROM zentriq.system_settings WHERE key = @k");
            del.Parameters.AddWithValue("k", InternetCheckKey);
            await del.ExecuteNonQueryAsync(ct);
        }
        else await SetSettingAsync(InternetCheckKey, JsonSerializer.Serialize(check, Json), ct);
    }

    /// <summary>Creates a session: its device group (consent always on) and an invitation valid for <paramref name="hours"/> hours.</summary>
    /// <param name="lan">The person is on the office network: the link uses the server's LAN address. Otherwise the link
    /// uses the verified internet address, and creating it fails with <see cref="InternetRequiredException"/> when there is none.</param>
    public async Task<QuickSession> CreateQuickAsync(Guid? userId, string userName, string? customer, string? email, string? note, int hours, string? browserHost, CancellationToken ct, bool lan = false)
    {
        hours = Math.Clamp(hours, 1, 72);
        var settings = await SettingsAsync(ct);
        if (!lan)
        {
            var check = await InternetCheckAsync(ct);
            if (RemoteServerConfig.NormalizeHost(settings.InternetHost) is null || check is null || !check.Usable)
                throw new InternetRequiredException(check?.Message);
        }
        string code;
        await using (var check = ds.CreateCommand("SELECT 1 FROM zentriq.remote_quick_sessions WHERE code = @c"))
        {
            var p = check.Parameters.Add("c", NpgsqlDbType.Text);
            do { code = NewQuickCode(); p.Value = code; } while (await check.ExecuteScalarAsync(ct) is not null);
        }
        string mesh, url;
        await using (var c = await ConnectAsync(ct))
        {
            mesh = await c.CreateGroupAsync(QuickGroupPrefix + FormatCode(code), $"Zentriq quick support session {FormatCode(code)}. Removed automatically when the session ends.", MeshRights.QuickConsent, ct);
            try
            {
                var cookie = await c.CreateInviteAsync(mesh, hours, flags: 1, ct); // run only: nothing is installed
                url = InviteBase(settings, browserHost, lan) + "agentinvite?c=" + Uri.EscapeDataString(cookie);
            }
            catch
            {
                try { await c.DeleteGroupAsync(mesh, CancellationToken.None); } catch (Exception ex) when (ex is MeshException or System.Net.WebSockets.WebSocketException) { }
                throw;
            }
        }
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.remote_quick_sessions (code, mesh_id, invite_url, customer, email, note, created_by, created_by_name, expires_at)
            VALUES (@code, @mesh, @url, @cu, @em, @no, @by, @byn, now() + make_interval(hours => @h)) RETURNING id
            """);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("mesh", mesh);
        cmd.Parameters.AddWithValue("url", url);
        cmd.Parameters.Add(new NpgsqlParameter("cu", NpgsqlDbType.Text) { Value = (object?)Trim(customer, 120) ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("em", NpgsqlDbType.Text) { Value = (object?)Trim(email, 254) ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("no", NpgsqlDbType.Text) { Value = (object?)Trim(note, 500) ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("byn", userName);
        cmd.Parameters.AddWithValue("h", hours);
        long id;
        try { id = (long)(await cmd.ExecuteScalarAsync(ct))!; }
        catch
        {
            // Do not leave a group without a session behind.
            try { await using var c = await ConnectAsync(CancellationToken.None); await c.DeleteGroupAsync(mesh, CancellationToken.None); }
            catch (Exception ex) when (ex is MeshException or System.Net.WebSockets.WebSocketException) { }
            throw;
        }
        logger.LogInformation("Quick support session {Code} created by {User}", code, userName);
        return (await QuickSessionAsync(id, ct))!;
    }

    /// <summary>The invitation message (e-mail or chat) for a quick support session.</summary>
    public static (string Subject, string Text) QuickInvitation(QuickSession q, string technician)
    {
        var code = RemoteSupportService.FormatCode(q.Code);
        var hi = string.IsNullOrWhiteSpace(q.Customer) ? "Hello," : $"Hello {q.Customer},";
        return ($"Remote support session {code}", $"""
            {hi}

            {technician} from IT would like to help you remotely (session {code}).

            1. Open this link: {q.InviteUrl}
            2. Download and run Zentriq Remote Support (nothing is installed; on a Mac or Linux PC choose your system on the page).
            3. Click Connect, then allow each request from {technician} when it appears. You can disconnect at any time.

            The link works until {q.ExpiresAt.ToLocalTime():g}.
            """);
    }

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();

    private const string QuickSelect = """
        SELECT id, code, invite_url, customer, email, note, created_by_name, created_at, expires_at, status, node_id, device_name, device_os, device_ip,
               online, connected_at, last_seen_at, ended_at, ended_by, end_reason
          FROM zentriq.remote_quick_sessions
        """;

    private static QuickSession ReadQuick(NpgsqlDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        DateTimeOffset? D(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
        return new QuickSession(r.GetInt64(0), r.GetString(1), r.GetString(2), S(3), S(4), S(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7),
            r.GetFieldValue<DateTimeOffset>(8), r.GetString(9), S(10), S(11), S(12), S(13), r.GetBoolean(14), D(15), D(16), D(17), S(18), S(19));
    }

    public async Task<QuickSession?> QuickSessionAsync(long id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(QuickSelect + " WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadQuick(r) : null;
    }

    /// <summary>Active sessions first, then the last 7 days.</summary>
    public async Task<List<QuickSession>> QuickSessionsAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(QuickSelect + """
             WHERE status IN ('waiting','connected') OR created_at > now() - interval '7 days'
             ORDER BY status IN ('waiting','connected') DESC, created_at DESC LIMIT 200
            """);
        var list = new List<QuickSession>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadQuick(r));
        return list;
    }

    /// <summary>The device group of an active session, or null.</summary>
    public async Task<(string MeshId, string? NodeId, bool Online, string Code, string? Device)?> ActiveQuickAsync(long id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT mesh_id, node_id, online, code, COALESCE(device_name, customer) FROM zentriq.remote_quick_sessions WHERE id = @id AND status IN ('waiting','connected') AND mesh_id IS NOT NULL");
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetBoolean(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
    }

    /// <summary>Ends a session: removes its device group (the app on the device disconnects) and records who ended it.</summary>
    public async Task<bool> EndQuickAsync(long id, string endedBy, string reason, CancellationToken ct, string status = "ended")
    {
        string? mesh;
        await using (var q = ds.CreateCommand("SELECT mesh_id FROM zentriq.remote_quick_sessions WHERE id = @id AND status IN ('waiting','connected')"))
        {
            q.Parameters.AddWithValue("id", id);
            var v = await q.ExecuteScalarAsync(ct);
            if (v is null) return false;
            mesh = v as string;
        }
        if (mesh is not null && Configured)
        {
            await using var c = await ConnectAsync(ct);
            await c.DeleteGroupAsync(mesh, ct);
        }
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.remote_quick_sessions SET status = @s, online = false, ended_at = now(), ended_by = @by, end_reason = @why
             WHERE id = @id AND status IN ('waiting','connected')
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("by", endedBy);
        cmd.Parameters.AddWithValue("why", reason);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Brings active sessions up to date with the remote support server: the device that joined (name, OS, address,
    /// online), and ends sessions that expired unused, whose device stayed offline (the person closed the app) or that
    /// reached the time limit. Returns the number of sessions changed.
    /// </summary>
    public async Task<int> RefreshQuickAsync(CancellationToken ct)
    {
        var active = new List<(long Id, string? Mesh, string Status, DateTimeOffset Created, DateTimeOffset Expires, DateTimeOffset? LastSeen)>();
        await using (var q = ds.CreateCommand("SELECT id, mesh_id, status, created_at, expires_at, last_seen_at FROM zentriq.remote_quick_sessions WHERE status IN ('waiting','connected')"))
        await using (var r = await q.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                active.Add((r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4),
                    r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5)));
        if (!Configured) return 0;

        Dictionary<string, string> groups;
        List<MeshNode> nodes;
        var changed = 0;
        await using (var c = await ConnectAsync(ct))
        {
            groups = await c.GroupsAsync(ct);
            // The engine brings a deleted group back when an agent of it connects again (for example the person runs
            // an old copy of the support app after the session ended). Remove quick support groups of no active session.
            var activeMeshes = active.Select(a => a.Mesh).Where(m => m is not null).ToHashSet(StringComparer.Ordinal);
            foreach (var (id, name) in groups.Where(g => g.Value.StartsWith(QuickGroupPrefix, StringComparison.Ordinal) && !activeMeshes.Contains(g.Key)).ToList())
            {
                await c.DeleteGroupAsync(id, ct);
                groups.Remove(id);
                logger.LogInformation("Quick support: removed group {Name} of an ended session", name);
                changed++;
            }
            if (active.Count == 0) return changed;
            nodes = await c.NodesAsync(null, ct);
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var s in active)
        {
            if (s.Mesh is null || !groups.ContainsKey(s.Mesh))
            {
                changed += await EndQuickAsync(s.Id, "system", "The session's device group no longer exists on the remote support server.", ct) ? 1 : 0;
                continue;
            }
            var node = nodes.Where(n => n.MeshId == s.Mesh).OrderByDescending(n => n.Connected).FirstOrDefault();
            if (node is null)
            {
                if (s.Status == "connected")
                {
                    // The support app runs as a temporary agent: the server forgets the device when the person closes it.
                    // Keep the session for a while so the person can open the link again, then end it.
                    await using (var off = ds.CreateCommand("UPDATE zentriq.remote_quick_sessions SET online = false WHERE id = @id AND online"))
                    {
                        off.Parameters.AddWithValue("id", s.Id);
                        changed += await off.ExecuteNonQueryAsync(ct);
                    }
                    if (now - (s.LastSeen ?? s.Created) > QuickOfflineLimit)
                        changed += await EndQuickAsync(s.Id, "system", "The person closed the support app.", ct) ? 1 : 0;
                    else if (now - s.Created > QuickMaxDuration) changed += await EndQuickAsync(s.Id, "system", "Time limit reached.", ct) ? 1 : 0;
                }
                else if (now > s.Expires) changed += await EndQuickAsync(s.Id, "system", "Nobody joined before the invitation expired.", ct, "expired") ? 1 : 0;
                else if (now - s.Created > QuickMaxDuration) changed += await EndQuickAsync(s.Id, "system", "Time limit reached.", ct) ? 1 : 0;
                continue;
            }
            await using (var u = ds.CreateCommand("""
                UPDATE zentriq.remote_quick_sessions
                   SET status = 'connected', node_id = @n, device_name = @name, device_os = @os, device_ip = COALESCE(@ip, device_ip), online = @on,
                       connected_at = COALESCE(connected_at, now()), last_seen_at = CASE WHEN @on THEN now() ELSE COALESCE(last_seen_at, now()) END
                 WHERE id = @id AND status IN ('waiting','connected')
                """))
            {
                u.Parameters.AddWithValue("id", s.Id);
                u.Parameters.AddWithValue("n", node.Id);
                u.Parameters.AddWithValue("name", node.Name);
                u.Parameters.Add(new NpgsqlParameter("os", NpgsqlDbType.Text) { Value = (object?)node.Os ?? DBNull.Value });
                u.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Text) { Value = (object?)node.Ip ?? DBNull.Value });
                u.Parameters.AddWithValue("on", node.Connected);
                await u.ExecuteNonQueryAsync(ct);
            }
            if (s.Status == "waiting") changed++;
            var lastSeen = node.Connected ? now : s.LastSeen ?? now;
            if (!node.Connected && now - lastSeen > QuickOfflineLimit)
                changed += await EndQuickAsync(s.Id, "system", "The person closed the support app (offline for 30 minutes).", ct) ? 1 : 0;
            else if (now - s.Created > QuickMaxDuration)
                changed += await EndQuickAsync(s.Id, "system", "Time limit reached.", ct) ? 1 : 0;
        }
        return changed;
    }
}
