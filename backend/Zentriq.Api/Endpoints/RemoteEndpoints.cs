using Zentriq.Infrastructure.Configuration;
using System.Net.WebSockets;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Mail;
using Zentriq.Infrastructure.Remote;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Zentriq Remote Support (Remote Management): remote desktop, terminal and file transfer through the bundled
/// MeshCentral engine (Apache-2.0) run by the ZentriqRemote service. Technicians sign in with a short-lived link
/// created here; the person at the device is asked for consent according to the remote support settings.
/// </summary>
public static class RemoteEndpoints
{
    public sealed record SessionBody(Guid? AssetId, string? NodeId, string View);
    public sealed record DeployBody(Guid[] AssetIds, string Operation);
    public sealed record MapBody(string NodeId, Guid? AssetId);
    public sealed record QuickBody(string? Customer, string? Email, string? Note, int? Hours, bool? SendEmail, bool? Lan = null);
    public sealed record InternetBody(string? Host, int? Port, bool? ConfirmedFromOutside, bool? Save);
    public sealed record QuickOpenBody(string View);
    public sealed record InstallLinkBody(int? Hours);

    private static bool Unavailable(Exception ex) => ex is MeshException or WebSocketException or HttpRequestException or TaskCanceledException or IOException;

    public static void MapRemoteEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/remote").WithTags("Remote support").RequireModule(Modules.Remote).RequireAuthorization(Permissions.RemoteStart);

        g.MapGet("/status", async (RemoteSupportService remote, NpgsqlDataSource ds, HttpContext http, ZentriqPaths paths, CancellationToken ct) =>
        {
            var st = await remote.StatusAsync(ct);
            var settings = await remote.SettingsAsync(ct);
            var inetCheck = await remote.InternetCheckAsync(ct);
            var inetHost = RemoteServerConfig.NormalizeHost(settings.InternetHost);
            var inetApplied = inetHost is not null && RemoteServerConfig.ReadLines(Path.Combine(paths.RemoteCertificateDir, RemoteServerConfig.AppliedOriginsFile)).Contains(inetHost);
            await using var cmd = ds.CreateCommand("""
                SELECT (SELECT count(*) FROM zentriq.remote_nodes), (SELECT count(*) FROM zentriq.remote_nodes WHERE connected),
                       (SELECT count(*) FROM zentriq.remote_nodes WHERE asset_id IS NULL),
                       (SELECT count(*) FROM zentriq.remote_sessions WHERE created_at > now() - interval '24 hours'),
                       (SELECT max(synced_at) FROM zentriq.remote_nodes),
                       (SELECT count(*) FROM zentriq.agent_registrations r WHERE r.status = 'active' AND 'remote-support' = ANY(r.capabilities)
                           AND NOT EXISTS (SELECT 1 FROM zentriq.remote_nodes n WHERE n.asset_id = r.asset_id)),
                       (SELECT count(*) FROM zentriq.remote_quick_sessions WHERE status IN ('waiting','connected')),
                       (SELECT count(*) FROM zentriq.remote_quick_sessions WHERE created_at > now() - interval '30 days')
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return Results.Ok(new
            {
                st.Installed, st.Running, st.Message, PublicUrl = st.Installed ? remote.UrlFor(http.Request.Host.Host) : null, st.Port,
                devices = r.GetInt64(0), online = r.GetInt64(1), unmapped = r.GetInt64(2), sessions24h = r.GetInt64(3),
                lastSync = r.IsDBNull(4) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(4), readyToInstall = r.GetInt64(5),
                quickActive = r.GetInt64(6), quick30d = r.GetInt64(7),
                internet = inetHost is null ? null : new
                {
                    host = inetHost, port = settings.InternetPort ?? st.Port, applied = inetApplied,
                    usable = inetCheck?.Usable == true, verified = inetCheck?.Ok == true, confirmed = inetCheck?.ConfirmedFromOutside == true,
                    checkedAt = inetCheck?.CheckedAt, message = inetCheck?.Message,
                },
            });
        });

        // Devices: every remote agent (mapped to its asset when possible), plus assets whose Zentriq agent can install it.
        g.MapGet("/devices", async (string? q, string? filter, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var list = new List<object>();
            await using var cmd = ds.CreateCommand("""
                SELECT n.node_id, n.name, n.os, n.ip, n.connected, n.last_connected_at, n.asset_id, a.hostname, a.category, d.name, a.os_name,
                       ag.id IS NOT NULL, COALESCE('remote-support' = ANY(ag.capabilities), false),
                       (SELECT x.status || '|' || COALESCE(x.detail, '') FROM zentriq.asset_actions x WHERE x.asset_id = COALESCE(n.asset_id, a.id) AND x.kind = 'remote-support' ORDER BY x.requested_at DESC LIMIT 1),
                       zentriq.asset_ipv4(n.asset_id)
                  FROM (SELECT node_id, name, os, ip, connected, last_connected_at, asset_id FROM zentriq.remote_nodes
                        UNION ALL
                        SELECT NULL, COALESCE(a2.hostname, host(a2.primary_ip)), a2.os_name, host(a2.primary_ip), false, NULL, a2.id
                          FROM zentriq.assets a2
                         WHERE a2.status = 'active' AND a2.os_family = 'windows'
                           AND NOT EXISTS (SELECT 1 FROM zentriq.remote_nodes n2 WHERE n2.asset_id = a2.id)) n
                  LEFT JOIN zentriq.assets a ON a.id = n.asset_id
                  LEFT JOIN zentriq.departments d ON d.id = a.department_id
                  LEFT JOIN LATERAL (SELECT r.id, r.capabilities FROM zentriq.agent_registrations r WHERE r.asset_id = n.asset_id AND r.status = 'active'
                                      ORDER BY r.last_checkin_at DESC NULLS LAST LIMIT 1) ag ON true
                 WHERE (@q::text IS NULL OR n.name ILIKE '%' || @q || '%' OR a.hostname ILIKE '%' || @q || '%' OR n.ip LIKE @q || '%')
                   AND (@f::text IS NULL OR (@f = 'installed' AND n.node_id IS NOT NULL) OR (@f = 'online' AND n.connected)
                        OR (@f = 'missing' AND n.node_id IS NULL) OR (@f = 'unmapped' AND n.node_id IS NOT NULL AND n.asset_id IS NULL))
                 ORDER BY n.node_id IS NULL, n.connected DESC, lower(n.name) LIMIT 2000
                """);
            cmd.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : q.Trim() });
            cmd.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Text) { Value = filter is "installed" or "online" or "missing" or "unmapped" ? filter : DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var last = S(13)?.Split('|', 2);
                list.Add(new
                {
                    id = S(0) ?? "asset:" + r.GetGuid(6), nodeId = S(0), name = r.GetString(1), os = S(2) ?? S(10), ip = AgentEndpoints.Ipv4(S(14), S(3)), online = r.GetBoolean(4),
                    lastConnectedAt = r.IsDBNull(5) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(5),
                    assetId = r.IsDBNull(6) ? (Guid?)null : r.GetGuid(6), hostname = S(7), category = S(8), department = S(9),
                    hasAgent = r.GetBoolean(11), canInstall = r.GetBoolean(12),
                    lastAction = last is null ? null : new { status = last[0], detail = last.Length > 1 ? last[1] : null },
                });
            }
            return Results.Ok(list);
        });

        // Remote access for one asset (device page): node and connection state.
        g.MapGet("/assets/{assetId:guid}", async (Guid assetId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT node_id, connected, last_connected_at FROM zentriq.remote_nodes WHERE asset_id = @a ORDER BY connected DESC, synced_at DESC LIMIT 1");
            cmd.Parameters.AddWithValue("a", assetId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.Ok(new { installed = false, online = false, nodeId = (string?)null, lastConnectedAt = (DateTimeOffset?)null });
            return Results.Ok(new { installed = true, online = r.GetBoolean(1), nodeId = r.GetString(0), lastConnectedAt = r.IsDBNull(2) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(2) });
        });

        // A short-lived sign-in link that opens the device in the remote support console.
        g.MapPost("/sessions", async (SessionBody body, NpgsqlDataSource ds, RemoteSupportService remote, RequestContext rc, HttpContext http, ZentriqPaths paths, CancellationToken ct) =>
        {
            if (body.View is not ("desktop" or "terminal" or "files" or "device" or "console")) return Validation.Problem("view", "Choose desktop, terminal, files, device or console.");
            if (rc.UserId is not { } uid) return Results.Unauthorized();
            string? nodeId = null, device = null;
            Guid? assetId = body.AssetId;
            if (body.View != "console")
            {
                await using var cmd = ds.CreateCommand("""
                    SELECT n.node_id, n.name, n.asset_id, n.connected FROM zentriq.remote_nodes n
                     WHERE (@n::text IS NOT NULL AND n.node_id = @n) OR (@n::text IS NULL AND n.asset_id = @a)
                     ORDER BY n.connected DESC, n.synced_at DESC LIMIT 1
                    """);
                cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = (object?)body.NodeId ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)body.AssetId ?? DBNull.Value });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.Problem(statusCode: 404, title: "This device does not have the Zentriq Remote Support agent yet. Install it from Remote Support › Deploy.");
                nodeId = r.GetString(0); device = r.GetString(1); assetId = r.IsDBNull(2) ? null : r.GetGuid(2);
            }
            else if (!rc.Has(Permissions.RemoteUnattended)) return Results.Problem(statusCode: 403, title: "Opening the full remote support console needs the 'Full remote support rights' permission.");

            var name = rc.Principal.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? rc.UserName;
            string url;
            try
            {
                await remote.EnsureOriginAsync(paths.RemoteCertificateDir, http.Request.Host.Host, ct);
                url = await remote.SessionUrlAsync(uid, name, rc.Has(Permissions.RemoteUnattended), viewOnly: false, nodeId, body.View, ct, http.Request.Host.Host);
            }
            catch (Exception ex) when (ex is MeshException or WebSocketException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(statusCode: 503, title: "Remote support is not available: " + ex.Message);
            }
            await using (var log = ds.CreateCommand("""
                INSERT INTO zentriq.remote_sessions (user_id, user_name, asset_id, node_id, device_name, view, source_ip) VALUES (@u, @n, @a, @node, @d, @v, @ip)
                """))
            {
                log.Parameters.AddWithValue("u", uid);
                log.Parameters.AddWithValue("n", name);
                log.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)assetId ?? DBNull.Value });
                log.Parameters.Add(new NpgsqlParameter("node", NpgsqlDbType.Text) { Value = (object?)nodeId ?? DBNull.Value });
                log.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = (object?)device ?? DBNull.Value });
                log.Parameters.AddWithValue("v", body.View);
                log.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Text) { Value = (object?)http.Connection.RemoteIpAddress?.ToString() ?? DBNull.Value });
                await log.ExecuteNonQueryAsync(ct);
            }
            await rc.AuditAsync("remote.session", device ?? "remote console", detail: new { body.View, assetId, nodeId });
            return Results.Ok(new { url, expiresInSeconds = 120 });
        });

        // ---- quick support (on demand, anywhere) ----------------------------------------------------------------

        g.MapGet("/quick", async (RemoteSupportService remote, CancellationToken ct) =>
        {
            string? warning = null;
            try { await remote.RefreshQuickAsync(ct); }
            catch (Exception ex) when (Unavailable(ex)) { warning = "Remote support server not reachable; showing the last known state. " + ex.Message; }
            return Results.Ok(new { items = await remote.QuickSessionsAsync(ct), warning });
        });

        g.MapPost("/quick", async (QuickBody body, RemoteSupportService remote, MailService mail, RequestContext rc, HttpContext http, ZentriqPaths paths, CancellationToken ct) =>
        {
            var email = body.Email?.Trim();
            if (!string.IsNullOrEmpty(email) && (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out _))) return Validation.Problem("email", "Enter a valid e-mail address or leave it empty.");
            var name = rc.Principal.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? rc.UserName;
            QuickSession q;
            try
            {
                await remote.EnsureOriginAsync(paths.RemoteCertificateDir, http.Request.Host.Host, ct);
                q = await remote.CreateQuickAsync(rc.UserId, name, body.Customer, email, body.Note, body.Hours ?? 1, http.Request.Host.Host, ct, lan: body.Lan == true);
            }
            catch (InternetRequiredException ex) { return Results.Problem(statusCode: 409, title: ex.Message, type: "internet-required"); }
            catch (Exception ex) when (Unavailable(ex)) { return Results.Problem(statusCode: 503, title: "Remote support is not available: " + ex.Message); }
            await rc.AuditAsync("remote.quick_create", RemoteSupportService.FormatCode(q.Code), detail: new { q.Id, q.Customer, q.Email, q.ExpiresAt });
            string? emailResult = null;
            if (body.SendEmail == true && !string.IsNullOrEmpty(email))
            {
                var smtp = await mail.LoadAsync(ct);
                if (!MailService.IsUsable(smtp)) emailResult = "E-mail is not set up (Settings › E-mail); copy the invitation instead.";
                else
                {
                    var (subject, text) = RemoteSupportService.QuickInvitation(q, name);
                    try { await mail.SendAsync(smtp, email, subject, text, null, ct); emailResult = "sent"; }
                    catch (Exception ex) when (ex is MailException or System.Net.Mail.SmtpException or InvalidOperationException or IOException) { emailResult = "The e-mail could not be sent: " + ex.Message; }
                }
            }
            var (subj, body2) = RemoteSupportService.QuickInvitation(q, name);
            return Results.Ok(new { session = q, email = emailResult, invitation = new { subject = subj, text = body2 } });
        });

        g.MapPost("/quick/{id:long}/open", async (long id, QuickOpenBody body, NpgsqlDataSource ds, RemoteSupportService remote, RequestContext rc, HttpContext http, ZentriqPaths paths, CancellationToken ct) =>
        {
            if (body.View is not ("desktop" or "files" or "terminal" or "device")) return Validation.Problem("view", "Choose desktop, files, terminal or device.");
            if (rc.UserId is not { } uid) return Results.Unauthorized();
            if (await remote.ActiveQuickAsync(id, ct) is not { } q) return Results.Problem(statusCode: 404, title: "This quick support session has ended.");
            if (q.NodeId is null) return Results.Problem(statusCode: 409, title: "Nobody has joined this session yet. Send the invitation link to the person who needs help.");
            if (!q.Online) return Results.Problem(statusCode: 409, title: "The person's support app is not connected (closed or offline).");
            var name = rc.Principal.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? rc.UserName;
            string url;
            try
            {
                await remote.EnsureOriginAsync(paths.RemoteCertificateDir, http.Request.Host.Host, ct);
                url = await remote.SessionUrlAsync(uid, name, fullRights: false, viewOnly: false, q.NodeId, body.View, ct, http.Request.Host.Host, groupId: q.MeshId);
            }
            catch (Exception ex) when (Unavailable(ex)) { return Results.Problem(statusCode: 503, title: "Remote support is not available: " + ex.Message); }
            await using (var log = ds.CreateCommand("""
                INSERT INTO zentriq.remote_sessions (user_id, user_name, node_id, device_name, view, source_ip, quick_session_id) VALUES (@u, @n, @node, @d, @v, @ip, @q)
                """))
            {
                log.Parameters.AddWithValue("u", uid);
                log.Parameters.AddWithValue("n", name);
                log.Parameters.AddWithValue("node", q.NodeId);
                log.Parameters.AddWithValue("d", $"{q.Device ?? "Quick support"} (quick support {RemoteSupportService.FormatCode(q.Code)})");
                log.Parameters.AddWithValue("v", body.View);
                log.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Text) { Value = (object?)http.Connection.RemoteIpAddress?.ToString() ?? DBNull.Value });
                log.Parameters.AddWithValue("q", id);
                await log.ExecuteNonQueryAsync(ct);
            }
            await rc.AuditAsync("remote.session", $"quick support {RemoteSupportService.FormatCode(q.Code)}", detail: new { body.View, quickSessionId = id, q.NodeId });
            return Results.Ok(new { url, expiresInSeconds = 120 });
        });

        g.MapPost("/quick/{id:long}/end", async (long id, RemoteSupportService remote, RequestContext rc, CancellationToken ct) =>
        {
            var name = rc.Principal.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? rc.UserName;
            try
            {
                if (!await remote.EndQuickAsync(id, name, "Ended by the technician.", ct)) return Results.NotFound();
            }
            catch (Exception ex) when (Unavailable(ex)) { return Results.Problem(statusCode: 503, title: "Remote support is not available: " + ex.Message); }
            await rc.AuditAsync("remote.quick_end", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Results.NoContent();
        });

        g.MapGet("/sessions", async (Guid? assetId, int? page, int? pageSize, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            await using var cmd = ds.CreateCommand("""
                SELECT s.id, s.created_at, s.user_name, s.view, s.device_name, s.asset_id, s.source_ip, count(*) OVER ()
                  FROM zentriq.remote_sessions s WHERE (@a::uuid IS NULL OR s.asset_id = @a)
                 ORDER BY s.id DESC LIMIT @l OFFSET @o
                """);
            cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)assetId ?? DBNull.Value });
            cmd.Parameters.AddWithValue("l", ps);
            cmd.Parameters.AddWithValue("o", (p - 1) * ps);
            var items = new List<object>();
            long total = 0;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                total = r.GetInt64(7);
                items.Add(new
                {
                    id = r.GetInt64(0), at = r.GetFieldValue<DateTimeOffset>(1), user = r.GetString(2), view = r.GetString(3), device = r.IsDBNull(4) ? null : r.GetString(4),
                    assetId = r.IsDBNull(5) ? (Guid?)null : r.GetGuid(5), ip = r.IsDBNull(6) ? null : r.GetString(6),
                });
            }
            return Results.Ok(new { items, total, page = p, pageSize = ps });
        });

        // ---- management -------------------------------------------------------------------------------------

        // Install or remove the remote support agent through each device's Zentriq Agent.
        g.MapPost("/deploy", async (DeployBody body, NpgsqlDataSource ds, RemoteSupportService remote, RequestContext rc, CancellationToken ct) =>
        {
            if (body.Operation is not ("install" or "uninstall")) return Validation.Problem("operation", "Choose install or uninstall.");
            var ids = body.AssetIds?.Distinct().Take(5000).ToArray() ?? [];
            if (ids.Length == 0) return Validation.Problem("assetIds", "Select devices.");
            string sha;
            try { (_, sha) = await remote.AgentInstallerAsync(ct); }
            catch (Exception ex) when (ex is MeshException or WebSocketException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(statusCode: 503, title: "Remote support is not available: " + ex.Message);
            }
            await using var q = ds.CreateCommand("""
                SELECT a.id, COALESCE(a.hostname, zentriq.asset_ipv4(a.id), host(a.primary_ip)), ag.id, COALESCE('remote-support' = ANY(ag.capabilities), false),
                       EXISTS (SELECT 1 FROM zentriq.asset_actions x WHERE x.asset_id = a.id AND x.kind = 'remote-support' AND x.status IN ('queued','sent','running')
                                  AND x.requested_at > now() - interval '1 hour')
                  FROM zentriq.assets a
                  LEFT JOIN LATERAL (SELECT r.id, r.capabilities FROM zentriq.agent_registrations r WHERE r.asset_id = a.id AND r.status = 'active'
                                      ORDER BY r.last_checkin_at DESC NULLS LAST LIMIT 1) ag ON true
                 WHERE a.id = ANY(@ids)
                """);
            q.Parameters.AddWithValue("ids", ids);
            var rows = new List<(Guid Asset, string Host, Guid? Agent, bool Capable, bool Pending)>();
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) rows.Add((r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetBoolean(3), r.GetBoolean(4)));
            int queued = 0, noAgent = 0, oldAgent = 0, pending = 0;
            foreach (var a in rows)
            {
                if (a.Agent is null) { noAgent++; continue; }
                if (!a.Capable) { oldAgent++; continue; }
                if (a.Pending) { pending++; continue; }
                await using var ins = ds.CreateCommand("""
                    INSERT INTO zentriq.asset_actions (asset_id, agent_id, channel, kind, target, operation, argument, requested_by, detail)
                    VALUES (@as, @ag, 'agent', 'remote-support', 'Zentriq Remote Support', @op, @sha, @by, @d)
                    """);
                ins.Parameters.AddWithValue("as", a.Asset);
                ins.Parameters.AddWithValue("ag", a.Agent.Value);
                ins.Parameters.AddWithValue("op", body.Operation);
                ins.Parameters.AddWithValue("sha", sha);
                ins.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
                ins.Parameters.AddWithValue("d", body.Operation == "install" ? "Install remote support requested." : "Remove remote support requested.");
                await ins.ExecuteNonQueryAsync(ct);
                queued++;
            }
            AgentPresence.Instance.Nudge();
            await rc.AuditAsync($"remote.{body.Operation}", $"{queued} device(s)", detail: new { queued, noAgent, oldAgent, pending });
            var parts = new List<string>();
            if (queued > 0) parts.Add($"{queued} device(s) will {(body.Operation == "install" ? "install" : "remove")} Zentriq Remote Support at their next agent check-in (set by the check-in interval).");
            if (pending > 0) parts.Add($"{pending} already have a request in progress.");
            if (oldAgent > 0) parts.Add($"{oldAgent} run an earlier Zentriq Agent: update the agent first (Agents › Update).");
            if (noAgent > 0) parts.Add($"{noAgent} have no Zentriq Agent: use the installer download or command on those devices.");
            return Results.Ok(new { queued, pending, oldAgent, noAgent, message = parts.Count > 0 ? string.Join(" ", parts) : "Nothing to do." });
        }).RequireAuthorization(Permissions.RemoteManage);

        // The branded installer, for devices without a Zentriq Agent (run as administrator: ZentriqRemoteSupport.exe -fullinstall).
        g.MapGet("/installer", async (RemoteSupportService remote, RequestContext rc, CancellationToken ct) =>
        {
            try
            {
                var (bytes, sha) = await remote.AgentInstallerAsync(ct);
                await rc.AuditAsync("remote.installer_download", null, detail: new { sha });
                return Results.File(bytes, "application/octet-stream", "ZentriqRemoteSupport.exe");
            }
            catch (Exception ex) when (ex is MeshException or WebSocketException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(statusCode: 503, title: "Remote support is not available: " + ex.Message);
            }
        }).RequireAuthorization(Permissions.RemoteManage);

        // An invitation page to install the agent for unattended access (Windows, macOS, Linux), e.g. for laptops off the network.
        g.MapPost("/install-link", async (InstallLinkBody body, RemoteSupportService remote, RequestContext rc, HttpContext http, CancellationToken ct) =>
        {
            var hours = Math.Clamp(body.Hours ?? 24, 1, 720);
            try
            {
                var url = await remote.InstallLinkAsync(hours, http.Request.Host.Host, ct);
                await rc.AuditAsync("remote.install_link", null, detail: new { hours });
                return Results.Ok(new { url, expiresAt = DateTimeOffset.UtcNow.AddHours(hours) });
            }
            catch (Exception ex) when (Unavailable(ex)) { return Results.Problem(statusCode: 503, title: "Remote support is not available: " + ex.Message); }
        }).RequireAuthorization(Permissions.RemoteManage);

        g.MapPost("/sync", async (RemoteSupportService remote, RequestContext rc, CancellationToken ct) =>
        {
            try
            {
                var r = await remote.SyncAsync(ct);
                return Results.Ok(new { r.Nodes, r.Connected, r.Mapped, message = $"{r.Nodes} device(s) with the remote agent, {r.Connected} online, {r.Mapped} matched to assets." });
            }
            catch (Exception ex) when (ex is MeshException or WebSocketException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(statusCode: 503, title: "Remote support is not available: " + ex.Message);
            }
        }).RequireAuthorization(Permissions.RemoteManage);

        // Match a remote agent to an asset by hand (when host names differ); null clears the manual match.
        g.MapPut("/devices/map", async (MapBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.remote_nodes SET asset_id = @a, mapped_by = CASE WHEN @a::uuid IS NULL THEN 'hostname' ELSE 'manual' END
                 WHERE node_id = @n AND (@a::uuid IS NULL OR EXISTS (SELECT 1 FROM zentriq.assets WHERE id = @a))
                """);
            cmd.Parameters.AddWithValue("n", body.NodeId);
            cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)body.AssetId ?? DBNull.Value });
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await rc.AuditAsync("remote.map", body.NodeId, detail: new { body.AssetId });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.RemoteManage);

        g.MapGet("/settings", async (RemoteSupportService remote, CancellationToken ct) => Results.Ok(await remote.SettingsAsync(ct)));

        // Internet access: checks that a public name/address reaches this server over HTTPS before it is used for links.
        g.MapGet("/internet", async (RemoteSupportService remote, CancellationToken ct) => Results.Ok(await remote.InternetCheckAsync(ct)));

        g.MapPost("/internet", async (InternetBody body, RemoteSupportService remote, RequestContext rc, ZentriqPaths paths, CancellationToken ct) =>
        {
            var port = body.Port ?? remote.Port;
            if (InternetCheck.Validate(body.Host, port, out var host) is { } problem) return Validation.Problem("host", problem);
            var result = await remote.CheckInternetAsync(host, port, body.ConfirmedFromOutside == true, ct);
            if (body.Save == true)
            {
                if (!result.Usable) return Results.Json(result, statusCode: 422);
                try { RemoteServerConfig.WriteInternet(paths.RemoteCertificateDir, new(host, port)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Problem(statusCode: 500, title: "Could not save the internet address for the remote support service: " + ex.Message); }
                await remote.SaveInternetAsync(result, ct);
                await rc.AuditAsync("remote.internet", $"{host}:{port}", detail: new { result.Ok, result.ConfirmedFromOutside, result.Message });
            }
            return Results.Ok(result);
        }).RequireAuthorization(Permissions.RemoteManage);

        g.MapDelete("/internet", async (RemoteSupportService remote, RequestContext rc, ZentriqPaths paths, CancellationToken ct) =>
        {
            try { RemoteServerConfig.WriteInternet(paths.RemoteCertificateDir, null); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            await remote.SaveInternetAsync(null, ct);
            await rc.AuditAsync("remote.internet", "removed");
            return Results.NoContent();
        }).RequireAuthorization(Permissions.RemoteManage);

        g.MapPut("/settings", async (RemoteSettings body, RemoteSupportService remote, RequestContext rc, ZentriqPaths paths, CancellationToken ct) =>
        {
            // The internet address is set (and checked) with PUT /remote/internet; keep it as it is.
            var current = await remote.SettingsAsync(ct);
            body.InternetHost = current.InternetHost;
            body.InternetPort = current.InternetPort;
            try { await remote.SaveSettingsAsync(body, ct); }
            catch (Exception ex) when (ex is MeshException or WebSocketException or HttpRequestException or TaskCanceledException)
            {
                return Results.Problem(statusCode: 503, title: "Saved, but the remote support server could not be updated now: " + ex.Message);
            }
            await rc.AuditAsync("remote.settings", null, detail: body);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.RemoteManage);
    }
}
