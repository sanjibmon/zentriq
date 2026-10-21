using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Agents;
using Zentriq.Infrastructure.Audit;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Infrastructure.Licensing;
using Zentriq.Inventory;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Zentriq Agent protocol (agent port, mutual TLS):
///   POST /api/agent/v1/enroll     enrollment token + CSR  → client certificate (no client cert required)
///   POST /api/agent/v1/checkin    heartbeat; returns schedule and current server pins
///   POST /api/agent/v1/inventory  gzip JSON InventoryDocument
///   POST /api/agent/v1/renew      new CSR → new client certificate (authenticated with the current one)
/// </summary>
public static class AgentEndpoints
{
    /// <summary>Check-in (connectivity) and inventory intervals offered in the console and the agent installer, in minutes.</summary>
    public static readonly int[] CheckinChoices = [5, 15, 30, 60, 120, 180];
    public static readonly int[] InventoryChoices = [30, 60, 120, 180, 360, 720, 1440];
    public const int MinCheckinMinutes = 2, MaxCheckinMinutes = 1440, MinInventoryMinutes = 30, MaxInventoryMinutes = 10080;
    private const long MaxInventoryCompressed = 8L * 1024 * 1024;
    private const long MaxInventoryJson = 48L * 1024 * 1024;

    public sealed record EnrollRequest(string Token, string Csr, string? Hostname, string? OsName, string? AgentVersion, string? SmbiosUuid,
        int? CheckinMinutes = null, int? InventoryMinutes = null);
    public sealed record HelloRequest(string? CodeId, string? Nonce);
    public sealed record RenewRequest(string Csr);
    public sealed record CheckinRequest(string? AgentVersion, string? Hostname, string? OsName, string? LastError, string[]? Capabilities = null,
        DateTimeOffset? OfflineSince = null, bool? UninstallProtected = null);

    /// <summary>System-wide agent settings (Agents › Settings).</summary>
    public sealed class AgentSettings
    {
        public int CheckinMinutes { get; set; } = 30;
        public int InventoryMinutes { get; set; } = 180;
        /// <summary>Uninstalling the agent needs a one-hour code from the console.</summary>
        public bool UninstallProtection { get; set; } = true;
    }

    public static int? ValidCheckin(int? m) => m is >= MinCheckinMinutes and <= MaxCheckinMinutes ? m : null;
    public static int? ValidInventory(int? m) => m is >= MinInventoryMinutes and <= MaxInventoryMinutes ? m : null;

    public static async Task<AgentSettings> LoadSettingsAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'agents.settings'");
        var s = await cmd.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<AgentSettings>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new() : new();
        s.CheckinMinutes = ValidCheckin(s.CheckinMinutes) ?? 30;
        s.InventoryMinutes = ValidInventory(s.InventoryMinutes) ?? 180;
        return s;
    }
    public sealed record ActionResult(bool Ok, string? State, string? Message);

    private sealed record AgentIdentity(Guid Id, Guid? AssetId, string Thumbprint);

    public static void MapAgentEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/agent/v1").WithTags("Agent protocol");

        // Short enrollment codes carry no server key pins: before sending the code, the agent asks the server to prove that
        // it knows the code, bound to the key it serves (see AgentCodes.EnrollProof).
        g.MapPost("/enroll/hello", async (HelloRequest req, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.CodeId) || req.CodeId.Length != AgentCodes.EnrollmentIdLength || string.IsNullOrWhiteSpace(req.Nonce) || req.Nonce.Length is < 16 or > 128)
                return Results.Problem(statusCode: 400, title: "Code id and nonce are required.");
            await using var cmd = ds.CreateCommand("""
                SELECT token_hash FROM zentriq.agent_enrollment_tokens
                 WHERE short_id = @id AND revoked_at IS NULL AND expires_at > now() AND (max_uses IS NULL OR uses < max_uses)
                """);
            cmd.Parameters.AddWithValue("id", req.CodeId.ToUpperInvariant());
            var pins = Pins(server);
            if (await cmd.ExecuteScalarAsync(ct) is not byte[] hash)
                return Results.Problem(statusCode: 403, title: "The enrollment code is invalid, expired, revoked or has reached its use limit.");
            return Results.Ok(new { pins, proof = AgentCodes.EnrollProof(hash, req.Nonce, pins), serverUrls = server.AlternateUrls() });
        }).RequireRateLimiting("agent-enroll").AllowAnonymous();

        g.MapPost("/enroll", async (EnrollRequest req, HttpContext http, NpgsqlDataSource ds, AgentServer server, AuditWriter audit, ILoggerFactory lf,
            Zentriq.Infrastructure.Secrets.CredentialProtector protector, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("Zentriq.Agents");
            var ca = server.Ca;
            if (ca is null) return Results.Problem(statusCode: 503, title: "Agent enrollment is not configured on this server. Run 'zentriqctl repair'.");
            if (string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.Csr)) return Results.Problem(statusCode: 400, title: "Token and certificate request are required.");
            var tokenText = AgentCodes.NormalizeEnrollment(req.Token) ?? req.Token.Trim();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tokenText));
            var defaults = await LoadSettingsAsync(ds, ct);

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            Guid tokenId;
            int checkin, inventory;
            await using (var cmd = new NpgsqlCommand("""
                SELECT id, checkin_minutes, inventory_minutes FROM zentriq.agent_enrollment_tokens
                 WHERE token_hash = @h AND revoked_at IS NULL AND expires_at > now() AND (max_uses IS NULL OR uses < max_uses)
                 FOR UPDATE
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("h", hash);
                await using var tr = await cmd.ExecuteReaderAsync(ct);
                if (!await tr.ReadAsync(ct))
                {
                    log.LogWarning("Agent enrollment rejected from {Ip}: invalid, expired, revoked or used-up token", http.Connection.RemoteIpAddress);
                    return Results.Problem(statusCode: 403, title: "The enrollment code is invalid, expired, revoked or has reached its use limit.");
                }
                tokenId = tr.GetGuid(0);
                // Intervals: chosen at installation, else the code's, else the system defaults.
                checkin = ValidCheckin(req.CheckinMinutes) ?? (tr.IsDBNull(1) ? (int?)null : tr.GetInt32(1)) ?? defaults.CheckinMinutes;
                inventory = ValidInventory(req.InventoryMinutes) ?? (tr.IsDBNull(2) ? (int?)null : tr.GetInt32(2)) ?? defaults.InventoryMinutes;
            }

            var agentId = Guid.NewGuid();
            var uninstallKey = AgentCodes.NewUninstallKey();
            X509Certificate2 cert;
            try { cert = AgentCertificateAuthority.Issue(ca, Convert.FromBase64String(req.Csr), agentId); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { return Results.Problem(statusCode: 400, title: ex.Message); }
            using (cert)
            {
                await using (var ins = new NpgsqlCommand("""
                    INSERT INTO zentriq.agent_registrations (id, agent_version, cert_thumbprint, cert_serial, cert_not_after, status, hostname, os_name, last_ip, token_id,
                                                            enrolled_at, last_checkin_at, checkin_minutes, inventory_minutes, online_until, uninstall_key_protected)
                    VALUES (@id, @v, @tp, @sn, @na, 'active', @h, @os, @ip, @tok, now(), now(), @ci, @inv, now() + interval '45 seconds', @uk)   -- then the live connection (or, older agents, the check-in) keeps it online
                    """, conn, tx))
                {
                    ins.Parameters.AddWithValue("ci", checkin);
                    ins.Parameters.AddWithValue("inv", inventory);
                    ins.Parameters.AddWithValue("uk", ProtectKey(protector, agentId, uninstallKey));
                    ins.Parameters.AddWithValue("id", agentId);
                    ins.Parameters.AddWithValue("v", Trunc(req.AgentVersion, 40) ?? "unknown");
                    ins.Parameters.AddWithValue("tp", cert.Thumbprint);
                    ins.Parameters.AddWithValue("sn", cert.SerialNumber);
                    ins.Parameters.AddWithValue("na", cert.NotAfter.ToUniversalTime());
                    ins.Parameters.Add(Text("h", Trunc(req.Hostname, 255)));
                    ins.Parameters.Add(Text("os", Trunc(req.OsName, 200)));
                    ins.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = (object?)ClientIp(http) ?? DBNull.Value });
                    ins.Parameters.AddWithValue("tok", tokenId);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await using (var upd = new NpgsqlCommand("UPDATE zentriq.agent_enrollment_tokens SET uses = uses + 1 WHERE id = @id", conn, tx))
                {
                    upd.Parameters.AddWithValue("id", tokenId);
                    await upd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                await audit.WriteAsync(new AuditActor(null, "agent", http.Connection.RemoteIpAddress?.ToString()), "agent.enroll", agentId.ToString(), detail: new { hostname = req.Hostname, tokenId }, ct: ct);
                log.LogInformation("Agent {AgentId} enrolled ({Host}) from {Ip}", agentId, req.Hostname, http.Connection.RemoteIpAddress);
                return Results.Ok(new
                {
                    agentId,
                    certificate = Convert.ToBase64String(cert.Export(X509ContentType.Cert)),
                    caCertificate = Convert.ToBase64String(ca.Export(X509ContentType.Cert)),
                    checkinMinutes = checkin,
                    inventoryMinutes = inventory,
                    checkinSeconds = checkin * 60,
                    inventoryHours = (inventory + 59) / 60,
                    serverPins = Pins(server),
                    serverUrls = server.AlternateUrls(),
                    uninstallProtection = defaults.UninstallProtection,
                    uninstallKey = defaults.UninstallProtection ? Convert.ToBase64String(uninstallKey) : null,
                });
            }
        }).RequireRateLimiting("agent-enroll").AllowAnonymous();

        // Live connection: the agent keeps one request open (renewed at once when it returns). Online from the moment it
        // arrives; offline the moment it is closed (agent stopped, device shut down, connection reset) or, when it dies
        // silently, once it has not been renewed for AgentPresence.OnlineGrace. Returns early with wake = true when work
        // was queued for the agent (it then checks in at once).
        g.MapPost("/presence", async (HttpContext http, NpgsqlDataSource ds, AgentServer server, AgentPresence presence,
            IHostApplicationLifetime life, ILoggerFactory lf, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            presence.Enter(agent.Id);
            var aborted = false;
            try
            {
                var deadline = DateTime.UtcNow + AgentPresence.Hold;
                var (wake, wasOffline) = await PresenceSeenAsync(ds, agent.Id, ClientIp(http), ct);
                var code = await LoadCodeForAllAsync(ds, ct);
                if (wasOffline) lf.CreateLogger("Zentriq.Agents").LogInformation("Agent {AgentId} connected (live)", agent.Id);
                while (!wake)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) break;
                    var nudged = presence.Nudged;
                    var done = await Task.WhenAny(nudged, Task.Delay(left, ct));
                    ct.ThrowIfCancellationRequested();
                    if (done != nudged) break;
                    wake = await PendingWorkAsync(ds, agent.Id, ct);
                    var now = await LoadCodeForAllAsync(ds, ct);
                    if (now?.Hash != code?.Hash) { code = now; break; }   // a new (or cancelled) uninstall code: deliver it now
                }
                var protection = (await LoadSettingsAsync(ds, ct)).UninstallProtection;
                return Results.Ok(new { wake, uninstallCode = CodeForAgents(code, protection), uninstallProtection = protection, holdSeconds = (int)AgentPresence.Hold.TotalSeconds, onlineSeconds = (int)AgentPresence.OnlineGrace.TotalSeconds });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                aborted = true;
                return Results.Empty;
            }
            finally
            {
                // The agent closed its live connection: offline now (not when the server itself is shutting down).
                if (presence.Leave(agent.Id) && aborted && !life.ApplicationStopping.IsCancellationRequested)
                {
                    try
                    {
                        await using var off = ds.CreateCommand("UPDATE zentriq.agent_registrations SET online_until = LEAST(online_until, now()) WHERE id = @id");
                        off.Parameters.AddWithValue("id", agent.Id);
                        await off.ExecuteNonQueryAsync(CancellationToken.None);
                        lf.CreateLogger("Zentriq.Agents").LogInformation("Agent {AgentId} disconnected", agent.Id);
                    }
                    catch (Exception ex) { lf.CreateLogger("Zentriq.Agents").LogWarning(ex, "Could not record that agent {AgentId} disconnected", agent.Id); }
                }
            }
        }).AllowAnonymous();

        // The agent is stopping (service stop, shutdown): offline now, without waiting for the connection to close.
        g.MapPost("/presence/leave", async (HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            await using var off = ds.CreateCommand("UPDATE zentriq.agent_registrations SET online_until = LEAST(online_until, now()) WHERE id = @id");
            off.Parameters.AddWithValue("id", agent.Id);
            await off.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        }).AllowAnonymous();

        g.MapPost("/checkin", async (CheckinRequest req, HttpContext http, NpgsqlDataSource ds, AgentServer server,
            Zentriq.Infrastructure.Secrets.CredentialProtector protector, ILoggerFactory lf, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            var settings = await LoadSettingsAsync(ds, ct);
            // An outage the agent reports after reconnecting (it could not reach the server since OfflineSince).
            var outageFrom = req.OfflineSince is { } off && off < DateTimeOffset.UtcNow && off > DateTimeOffset.UtcNow.AddDays(-365) ? off : (DateTimeOffset?)null;
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.agent_registrations
                   SET last_checkin_at = now(), last_ip = @ip, agent_version = COALESCE(@v, agent_version),
                       hostname = COALESCE(@h, hostname), os_name = COALESCE(@os, os_name), capabilities = COALESCE(@cap, capabilities),
                       -- Agents with a live connection are online exactly while it is open (see /presence).
                       online_until = CASE WHEN 'presence' = ANY(COALESCE(@cap, ARRAY[]::text[])) THEN online_until
                                           ELSE now() + make_interval(mins => checkin_minutes * 2 + 5) END,
                       last_outage_from = COALESCE(@off, last_outage_from), last_outage_to = CASE WHEN @off::timestamptz IS NULL THEN last_outage_to ELSE now() END,
                       uninstall_protected = COALESCE(@up, uninstall_protected)
                 WHERE id = @id
                RETURNING last_inventory_at, cert_not_after, checkin_minutes, inventory_minutes, uninstall_key_protected
                """);
            cmd.Parameters.Add(new NpgsqlParameter("off", NpgsqlDbType.TimestampTz) { Value = (object?)outageFrom ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("up", NpgsqlDbType.Boolean) { Value = (object?)req.UninstallProtected ?? DBNull.Value });
            cmd.Parameters.AddWithValue("id", agent.Id);
            cmd.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = (object?)ClientIp(http) ?? DBNull.Value });
            cmd.Parameters.Add(Text("v", Trunc(req.AgentVersion, 40)));
            cmd.Parameters.Add(Text("h", Trunc(req.Hostname, 255)));
            cmd.Parameters.Add(Text("os", Trunc(req.OsName, 200)));
            cmd.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (object?)req.Capabilities?.Take(20).Select(c => Trunc(c, 40)!).ToArray() ?? DBNull.Value });
            DateTimeOffset? lastInv = null, notAfter = null;
            int checkin = settings.CheckinMinutes, inventory = settings.InventoryMinutes;
            string? keyProtected = null;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct))
                {
                    lastInv = r.IsDBNull(0) ? null : r.GetFieldValue<DateTimeOffset>(0);
                    notAfter = r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1);
                    checkin = r.GetInt32(2);
                    inventory = r.GetInt32(3);
                    keyProtected = r.IsDBNull(4) ? null : r.GetString(4);
                }
            if (outageFrom is not null) lf.CreateLogger("Zentriq.Agents").LogInformation("Agent {AgentId} reconnected after being offline since {Since:u}", agent.Id, outageFrom);
            // The device's uninstall key (created here for agents enrolled before uninstall protection existed).
            byte[]? uninstallKey = null;
            if (settings.UninstallProtection)
            {
                if (keyProtected is not null) uninstallKey = UnprotectKey(protector, agent.Id, keyProtected);
                if (uninstallKey is null)
                {
                    uninstallKey = AgentCodes.NewUninstallKey();
                    await using var k = ds.CreateCommand("UPDATE zentriq.agent_registrations SET uninstall_key_protected = @k, uninstall_protected = false WHERE id = @id");
                    k.Parameters.AddWithValue("id", agent.Id);
                    k.Parameters.AddWithValue("k", ProtectKey(protector, agent.Id, uninstallKey));
                    await k.ExecuteNonQueryAsync(ct);
                }
            }
            var inventoryDue = lastInv is null || lastInv < DateTimeOffset.UtcNow.AddMinutes(-inventory) || await RequestedAsync(ds, agent.Id, ct);
            // Pending device actions (service start/stop/restart). Actions older than 1 hour are not handed out any more.
            var actions = new List<object>();
            var caps = req.Capabilities ?? [];
            var kinds = new List<string>();
            if (caps.Contains("service-control")) kinds.Add("service");
            if (caps.Contains("software-uninstall")) kinds.Add("uninstall");
            if (caps.Contains("self-update")) kinds.Add("agent-update");
            if (caps.Contains("remote-support")) kinds.Add("remote-support");
            await CompleteUpdatesAsync(ds, agent.Id, req.AgentVersion, ct);
            if (kinds.Count > 0)
            {
                // Hand out new actions, and re-send ones whose result never arrived (e.g. the device restarted mid-action).
                await using var act = ds.CreateCommand("""
                    UPDATE zentriq.asset_actions SET status = 'sent', sent_at = now()
                     WHERE agent_id = @a AND channel = 'agent' AND kind = ANY(@k) AND requested_at > now() - interval '24 hours'
                       AND (status = 'queued' OR (status = 'sent' AND sent_at < now() - interval '30 minutes'))
                    RETURNING id, kind, target, operation, argument
                    """);
                act.Parameters.AddWithValue("a", agent.Id);
                act.Parameters.AddWithValue("k", kinds.ToArray());
                await using var ar = await act.ExecuteReaderAsync(ct);
                while (await ar.ReadAsync(ct))
                    actions.Add(new { id = ar.GetGuid(0), kind = ar.GetString(1), target = ar.GetString(2), operation = ar.GetString(3), argument = ar.IsDBNull(4) ? null : ar.GetString(4) });
            }
            return Results.Ok(new
            {
                actions,
                agentId = agent.Id,
                checkinMinutes = checkin,
                inventoryMinutes = inventory,
                checkinSeconds = checkin * 60,               // agents before build 63
                inventoryHours = (inventory + 59) / 60,
                inventoryDue,
                renewCertificate = notAfter is { } na && na < DateTimeOffset.UtcNow.AddDays(30),
                serverPins = Pins(server),
                serverUrls = server.AlternateUrls(),
                uninstallProtection = settings.UninstallProtection,
                // Always sent while protection is on (mutually authenticated channel only), so a key that was re-created
                // reaches the agent; the agent stores it readable by SYSTEM only.
                uninstallKey = settings.UninstallProtection && uninstallKey is not null ? Convert.ToBase64String(uninstallKey) : null,
                uninstallCode = CodeForAgents(await LoadCodeForAllAsync(ds, ct), settings.UninstallProtection),
            });
        }).AllowAnonymous();

        g.MapPost("/actions/{actionId:guid}/result", async (Guid actionId, ActionResult body, HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            var ok = await AssetActionEndpoints.CompleteAsync(ds, actionId, agent.Id, body.Ok, Trunc(body.State, 40), Trunc(body.Message, 2000), ct);
            return ok ? Results.NoContent() : Results.NotFound();
        }).AllowAnonymous();

        // The agent package for console-requested updates (client certificate required, like every agent call).
        g.MapGet("/package", async (HttpContext http, NpgsqlDataSource ds, AgentServer server, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            var file = env.WebRootFileProvider.GetFileInfo(PackagePath);
            if (!file.Exists || file.PhysicalPath is null) return Results.Problem(statusCode: 404, title: "No agent package is available on this server.");
            return Results.File(file.PhysicalPath, "application/x-msi", "ZentriqAgent-x64.msi");
        }).AllowAnonymous();

        // The branded remote support agent installer for this server (requested by "install remote support" actions).
        g.MapGet("/remote-agent", async (HttpContext http, NpgsqlDataSource ds, AgentServer server, Zentriq.Infrastructure.Remote.RemoteSupportService remote, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            try
            {
                var (bytes, _) = await remote.AgentInstallerAsync(ct);
                return Results.File(bytes, "application/octet-stream", "ZentriqRemoteSupport.exe");
            }
            catch (Exception ex) when (ex is Zentriq.Infrastructure.Remote.MeshException or HttpRequestException or System.Net.WebSockets.WebSocketException or TaskCanceledException)
            {
                return Results.Problem(statusCode: 503, title: "Remote support is not available on the server: " + ex.Message);
            }
        }).AllowAnonymous();

        g.MapPost("/inventory", async (HttpContext http, NpgsqlDataSource ds, AgentServer server, InventoryStore store, LicensingService licensing, ILoggerFactory lf, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = MaxInventoryCompressed;

            InventoryDocument? doc;
            try
            {
                Stream body = http.Request.Body;
                if (string.Equals(http.Request.Headers.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
                    body = new GZipStream(body, CompressionMode.Decompress);
                await using var limited = new LimitedStream(body, MaxInventoryJson);
                doc = await JsonSerializer.DeserializeAsync<InventoryDocument>(limited, InventoryStore.Json, ct);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or BadHttpRequestException or IOException)
            {
                return Results.Problem(statusCode: 400, title: "Inventory payload is not valid: " + ex.Message);
            }
            if (doc is null || doc.Schema < 1) return Results.Problem(statusCode: 400, title: "Inventory payload is empty.");
            doc.Source = "agent";
            if (doc.CollectedAt > DateTimeOffset.UtcNow.AddMinutes(10) || doc.CollectedAt < DateTimeOffset.UtcNow.AddDays(-30)) doc.CollectedAt = DateTimeOffset.UtcNow;

            var license = await licensing.GetStatusAsync(ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var result = await store.ApplyAsync(conn, doc, new MatchHints(PreferredAssetId: agent.AssetId, Ip: http.Connection.RemoteIpAddress, AgentId: agent.Id), license, ct);
            if (result.AssetId is null)
            {
                lf.CreateLogger("Zentriq.Agents").LogWarning("Inventory from agent {AgentId} not stored: {Note}", agent.Id, result.Note);
                return Results.Problem(statusCode: 409, title: result.Note ?? "Inventory was not stored.");
            }
            await using (var upd = new NpgsqlCommand("""
                UPDATE zentriq.agent_registrations SET asset_id = @a, last_inventory_at = now(), last_checkin_at = now() WHERE id = @id;
                -- A device enrolled again (agent reinstalled with a new code) replaces its earlier registration.
                UPDATE zentriq.agent_registrations o SET status = 'replaced', revoked_at = now()
                  FROM zentriq.agent_registrations n
                 WHERE n.id = @id AND o.id <> n.id AND o.status = 'active' AND o.asset_id = @a AND o.enrolled_at < n.enrolled_at
                   AND lower(COALESCE(o.hostname, '')) = lower(COALESCE(n.hostname, ''));
                DELETE FROM zentriq.system_settings WHERE key = @req;
                """, conn))
            {
                upd.Parameters.AddWithValue("a", result.AssetId.Value);
                upd.Parameters.AddWithValue("id", agent.Id);
                upd.Parameters.AddWithValue("req", RequestKey(agent.Id));
                await upd.ExecuteNonQueryAsync(ct);
            }
            return Results.Ok(new { assetId = result.AssetId, created = result.Created, software = doc.Software.Count });
        }).AllowAnonymous();

        g.MapPost("/renew", async (RenewRequest req, HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            X509Certificate2 cert;
            try { cert = AgentCertificateAuthority.Issue(server.Ca!, Convert.FromBase64String(req.Csr), agent.Id); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { return Results.Problem(statusCode: 400, title: ex.Message); }
            using (cert)
            {
                await using var cmd = ds.CreateCommand("UPDATE zentriq.agent_registrations SET cert_thumbprint = @tp, cert_serial = @sn, cert_not_after = @na WHERE id = @id AND cert_thumbprint = @old");
                cmd.Parameters.AddWithValue("tp", cert.Thumbprint);
                cmd.Parameters.AddWithValue("sn", cert.SerialNumber);
                cmd.Parameters.AddWithValue("na", cert.NotAfter.ToUniversalTime());
                cmd.Parameters.AddWithValue("id", agent.Id);
                cmd.Parameters.AddWithValue("old", agent.Thumbprint);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.Problem(statusCode: 409, title: "The certificate was already renewed.");
                return Results.Ok(new { certificate = Convert.ToBase64String(cert.Export(X509ContentType.Cert)) });
            }
        }).AllowAnonymous();
    }

    // ---- Admin API ---------------------------------------------------------------------------------

    public sealed record TokenRequest(string Name, int? ValidDays, int? MaxUses, string? ServerUrl, int? CheckinMinutes = null, int? InventoryMinutes = null);

    /// <summary>The silent install command for a code: server, code and the intervals chosen for the devices.</summary>
    public static string InstallCommand(string serverUrl, string code, int checkin, int inventory) =>
        $"msiexec /i ZentriqAgent-x64.msi /qn SERVERURL=\"{serverUrl}\" ENROLLMENTCODE=\"{code}\" CHECKININTERVAL={checkin} INVENTORYINTERVAL={inventory}";

    public static void MapAgentAdminEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agents").WithTags("Agents");

        g.MapGet("", async (NpgsqlDataSource ds, IWebHostEnvironment env, AgentPresence presence, CancellationToken ct) =>
        {
            var latest = PackageVersion(env);
            await using var cmd = ds.CreateCommand("""
                SELECT r.id, r.status, r.agent_version, r.hostname, r.os_name, host(r.last_ip), r.enrolled_at, r.last_checkin_at, r.last_inventory_at,
                       r.cert_not_after, r.asset_id, a.hostname, r.revoked_at, t.name, r.capabilities,
                       u.status, u.target, u.detail, u.requested_at, u.completed_at,
                       r.checkin_minutes, r.inventory_minutes, COALESCE(r.online_until > now(), false), r.last_outage_from, r.last_outage_to, r.uninstall_protected,
                       host(a.primary_ip)
                  FROM zentriq.agent_registrations r
                  LEFT JOIN LATERAL (SELECT x.status, x.target, x.detail, x.requested_at, x.completed_at FROM zentriq.asset_actions x
                                      WHERE x.agent_id = r.id AND x.kind = 'agent-update' ORDER BY x.requested_at DESC LIMIT 1) u ON true
                  LEFT JOIN zentriq.assets a ON a.id = r.asset_id
                  LEFT JOIN zentriq.agent_enrollment_tokens t ON t.id = r.token_id
                 ORDER BY r.status, COALESCE(r.hostname, a.hostname)
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                DateTimeOffset? D(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                var lastCheckin = D(7);
                var status = r.GetString(1);
                var health = status == "revoked" ? "revoked" : status == "replaced" ? "replaced"
                    : lastCheckin is null ? "never"
                    : r.GetBoolean(22) ? "online"
                    : lastCheckin > DateTimeOffset.UtcNow.AddDays(-7) ? "stale" : "offline";
                list.Add(new
                {
                    id = r.GetGuid(0), status, health, version = r.GetString(2), hostname = S(3) ?? S(11), osName = S(4), lastIp = Ipv4(S(26), S(5)),
                    enrolledAt = r.GetFieldValue<DateTimeOffset>(6), lastCheckinAt = lastCheckin, lastInventoryAt = D(8), certificateExpires = D(9),
                    assetId = r.IsDBNull(10) ? (Guid?)null : r.GetGuid(10), revokedAt = D(12), enrollmentCode = S(13),
                    canSelfUpdate = !r.IsDBNull(14) && r.GetFieldValue<string[]>(14).Contains("self-update"),
                    updateAvailable = status == "active" && IsOlder(r.GetString(2), latest),
                    latestVersion = latest,
                    update = r.IsDBNull(15) ? null : new { status = r.GetString(15), target = r.GetString(16), detail = S(17), requestedAt = r.GetFieldValue<DateTimeOffset>(18), completedAt = D(19) },
                    checkinMinutes = r.GetInt32(20), inventoryMinutes = r.GetInt32(21), online = r.GetBoolean(22),
                    lastOutage = D(23) is { } of ? new { from = of, to = D(24) } : null, uninstallProtected = r.GetBoolean(25),
                    live = !r.IsDBNull(14) && r.GetFieldValue<string[]>(14).Contains("presence"),
                    connected = presence.IsConnected(r.GetGuid(0)),
                });
            }
            return Results.Ok(list);
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/{id:guid}/revoke", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.agent_registrations SET status = 'revoked', revoked_at = now(), revoked_by = @by WHERE id = @id AND status <> 'revoked' RETURNING id
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            if (await cmd.ExecuteScalarAsync(ct) is null) return Results.NotFound();
            await rc.AuditAsync("agent.revoke", id.ToString());
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AgentsManage);

        g.MapPost("/{id:guid}/collect", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.system_settings (key, value) SELECT @k, to_jsonb(now()) WHERE EXISTS (SELECT 1 FROM zentriq.agent_registrations WHERE id = @id AND status = 'active')
                ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = now()
                """);
            cmd.Parameters.AddWithValue("k", RequestKey(id));
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await rc.AuditAsync("agent.collect_requested", id.ToString());
            AgentPresence.Instance.Nudge();
            return Results.Accepted(value: new { message = "Inventory requested: an agent that is online sends it within a minute, otherwise at its next check-in." });
        }).RequireAuthorization(Permissions.AgentsManage);

        // Update agents to the package served by this server. Agents of earlier builds without the self-update
        // capability are reported back: they need the new MSI installed once (after that they update from here).
        g.MapPost("/update", async (UpdateRequest body, NpgsqlDataSource ds, IWebHostEnvironment env, RequestContext rc, CancellationToken ct) =>
        {
            var file = env.WebRootFileProvider.GetFileInfo(PackagePath);
            var latest = PackageVersion(env);
            if (!file.Exists || file.PhysicalPath is null || latest is null) return Results.Problem(statusCode: 409, title: "No agent package is available on this server.");
            var ids = body.AgentIds?.Distinct().Take(20000).ToArray() ?? [];
            if (ids.Length == 0 && body.All != true) return Validation.Problem("agentIds", "Select agents, or update all agents.");
            var sha = PackageSha256(file.PhysicalPath);

            await using var q = ds.CreateCommand("""
                SELECT r.id, r.asset_id, r.agent_version, COALESCE(r.hostname, a.hostname), COALESCE('self-update' = ANY(r.capabilities), false),
                       EXISTS (SELECT 1 FROM zentriq.asset_actions x WHERE x.agent_id = r.id AND x.kind = 'agent-update' AND x.status IN ('queued','sent','running')
                                  AND x.requested_at > now() - interval '24 hours')
                  FROM zentriq.agent_registrations r LEFT JOIN zentriq.assets a ON a.id = r.asset_id
                 WHERE r.status = 'active' AND (@all OR r.id = ANY(@ids))
                """);
            q.Parameters.AddWithValue("all", body.All == true);
            q.Parameters.AddWithValue("ids", ids);
            var rows = new List<(Guid Id, Guid? Asset, string Version, string? Host, bool Capable, bool Pending)>();
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    rows.Add((r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4), r.GetBoolean(5)));

            int queued = 0, current = 0, pending = 0, manual = 0, noAsset = 0;
            var manualHosts = new List<string>();
            foreach (var a in rows)
            {
                if (!IsOlder(a.Version, latest)) { current++; continue; }
                if (a.Pending) { pending++; continue; }
                if (!a.Capable) { manual++; manualHosts.Add(a.Host ?? a.Id.ToString()); continue; }
                if (a.Asset is null) { noAsset++; continue; }
                await using var ins = ds.CreateCommand("""
                    INSERT INTO zentriq.asset_actions (asset_id, agent_id, channel, kind, target, operation, argument, requested_by, detail)
                    VALUES (@as, @ag, 'agent', 'agent-update', @t, 'update', @sha, @by, @d)
                    """);
                ins.Parameters.AddWithValue("as", a.Asset.Value);
                ins.Parameters.AddWithValue("ag", a.Id);
                ins.Parameters.AddWithValue("t", latest);
                ins.Parameters.AddWithValue("sha", sha);
                ins.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
                ins.Parameters.AddWithValue("d", $"Update {a.Version} -> {latest} requested.");
                await ins.ExecuteNonQueryAsync(ct);
                queued++;
            }
            AgentPresence.Instance.Nudge();
            await rc.AuditAsync("agent.update_requested", latest, detail: new { queued, current, pending, manual, noAsset, all = body.All == true });
            var parts = new List<string>();
            if (queued > 0) parts.Add($"{queued} agent(s) will update to {latest} at their next check-in (set by their check-in interval).");
            if (pending > 0) parts.Add($"{pending} already have an update in progress.");
            if (current > 0) parts.Add($"{current} are already up to date.");
            if (manual > 0) parts.Add($"{manual} run an earlier agent that cannot update itself ({string.Join(", ", manualHosts.Take(5))}{(manual > 5 ? ", …" : "")}): install the new ZentriqAgent-x64.msi on them once; later updates then work from here.");
            if (noAsset > 0) parts.Add($"{noAsset} have not sent inventory yet and were skipped.");
            return Results.Ok(new { queued, current, pending, manual, noAsset, version = latest, message = parts.Count > 0 ? string.Join(" ", parts) : "No agents selected." });
        }).RequireAuthorization(Permissions.AgentsManage);

        // Check-in and inventory intervals of selected agents (applied at their next check-in).
        g.MapPut("/schedule", async (ScheduleRequest body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (ValidCheckin(body.CheckinMinutes) is not { } ci) return Validation.Problem("checkinMinutes", $"Choose a check-in interval between {MinCheckinMinutes} minutes and 24 hours.");
            if (ValidInventory(body.InventoryMinutes) is not { } inv) return Validation.Problem("inventoryMinutes", "Choose an inventory interval between 30 minutes and 7 days.");
            if (inv < ci) return Validation.Problem("inventoryMinutes", "Inventory cannot be sent more often than the agent checks in.");
            var ids = body.AgentIds?.Distinct().Take(20000).ToArray() ?? [];
            if (ids.Length == 0 && body.All != true) return Validation.Problem("agentIds", "Select agents, or apply to all agents.");
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.agent_registrations SET checkin_minutes = @ci, inventory_minutes = @inv
                 WHERE status = 'active' AND (@all OR id = ANY(@ids))
                """);
            cmd.Parameters.AddWithValue("ci", ci);
            cmd.Parameters.AddWithValue("inv", inv);
            cmd.Parameters.AddWithValue("all", body.All == true);
            cmd.Parameters.AddWithValue("ids", ids);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("agent.schedule", $"{n} agent(s)", detail: new { ci, inv, all = body.All == true });
            return Results.Ok(new { updated = n, message = $"{n} agent(s) will check in every {Minutes(ci)} and send inventory every {Minutes(inv)}, from their next check-in." });
        }).RequireAuthorization(Permissions.AgentsManage);

        g.MapGet("/settings", async (NpgsqlDataSource ds, CancellationToken ct) =>
            Results.Ok(new { settings = await LoadSettingsAsync(ds, ct), checkinChoices = CheckinChoices, inventoryChoices = InventoryChoices }))
            .RequireAuthorization(Permissions.AssetsView);

        g.MapPut("/settings", async (AgentSettings body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (ValidCheckin(body.CheckinMinutes) is null) return Validation.Problem("checkinMinutes", "Choose a check-in interval.");
            if (ValidInventory(body.InventoryMinutes) is null || body.InventoryMinutes < body.CheckinMinutes) return Validation.Problem("inventoryMinutes", "Choose an inventory interval at least as long as the check-in interval.");
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.system_settings (key, value) VALUES ('agents.settings', @v::jsonb)
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
                """);
            cmd.Parameters.AddWithValue("v", JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("agent.settings", null, detail: body);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AgentsManage);

        // A one-hour code that allows uninstalling the agent on one device (uninstall protection).
        // One uninstall code for all agents: random, valid for one hour, sent to every agent (live connection: within
        // seconds; others when they next connect). The agents keep only its hash, readable by SYSTEM.
        g.MapGet("/uninstall-code", async (NpgsqlDataSource ds, Zentriq.Infrastructure.Secrets.CredentialProtector protector, CancellationToken ct) =>
        {
            var c = await LoadCodeForAllAsync(ds, ct);
            var settings = await LoadSettingsAsync(ds, ct);
            return Results.Ok(new { protection = settings.UninstallProtection, active = c is not null, code = c is null ? null : ShowCode(protector, c), expiresAt = c?.ExpiresAt, createdBy = c?.CreatedBy, createdAt = c?.CreatedAt });
        }).RequireAuthorization(Permissions.AgentsManage);

        g.MapPost("/uninstall-code", async (NpgsqlDataSource ds, RequestContext rc, Zentriq.Infrastructure.Secrets.CredentialProtector protector, CancellationToken ct) =>
        {
            var code = AgentCodes.NewUninstallCodeForAll();
            var c = new CodeForAll(AgentCodes.UninstallCodeHash(code), DateTimeOffset.UtcNow.Add(AgentCodes.UninstallValidity),
                protector.Protect(UninstallCodeAad, new Zentriq.Infrastructure.Secrets.CredentialSecret { Password = code }), rc.UserName, DateTimeOffset.UtcNow);
            await using (var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.system_settings (key, value) VALUES ('agents.uninstall_code', @v::jsonb)
                ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()
                """))
            {
                cmd.Parameters.AddWithValue("v", JsonSerializer.Serialize(c, JsonWeb));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            AgentPresence.Instance.Nudge();   // online agents receive it within seconds
            await rc.AuditAsync("agent.uninstall_code", "all agents", detail: new { expires = c.ExpiresAt });
            var shown = AgentCodes.Group(code, 4);
            return Results.Ok(new { active = true, code = shown, expiresAt = c.ExpiresAt, createdBy = c.CreatedBy, createdAt = c.CreatedAt });
        }).RequireAuthorization(Permissions.AgentsManage);

        g.MapDelete("/uninstall-code", async (NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.system_settings WHERE key = 'agents.uninstall_code'");
            await cmd.ExecuteNonQueryAsync(ct);
            AgentPresence.Instance.Nudge();
            await rc.AuditAsync("agent.uninstall_code_cancel", "all agents");
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AgentsManage);

        g.MapGet("/enrollment-tokens", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT t.id, t.name, t.server_url, t.max_uses, t.uses, t.expires_at, t.revoked_at, t.created_at, u.display_name
                  FROM zentriq.agent_enrollment_tokens t LEFT JOIN zentriq.users u ON u.id = t.created_by ORDER BY t.created_at DESC
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var expires = r.GetFieldValue<DateTimeOffset>(5);
                var revoked = r.IsDBNull(6) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(6);
                int? max = r.IsDBNull(3) ? null : r.GetInt32(3);
                var uses = r.GetInt32(4);
                var state = revoked is not null ? "revoked" : expires < DateTimeOffset.UtcNow ? "expired" : max is { } m && uses >= m ? "used" : "active";
                list.Add(new { id = r.GetGuid(0), name = r.GetString(1), serverUrl = r.GetString(2), maxUses = max, uses, expiresAt = expires, revokedAt = revoked, createdAt = r.GetFieldValue<DateTimeOffset>(7), createdBy = r.IsDBNull(8) ? null : r.GetString(8), state });
            }
            return Results.Ok(list);
        }).RequireAuthorization(Permissions.AgentsManage);

        g.MapPost("/enrollment-tokens", async (TokenRequest req, NpgsqlDataSource ds, AgentServer server, RequestContext rc, IWebHostEnvironment env, Zentriq.Infrastructure.Secrets.CredentialProtector protector, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 100) return Validation.Problem("name", "Enter a name (max 100 characters).");
            var days = req.ValidDays ?? 30;
            if (days is < 1 or > 365) return Validation.Problem("validDays", "Validity must be 1–365 days.");
            if (req.MaxUses is < 1 or > 100000) return Validation.Problem("maxUses", "Use limit must be 1–100000 (or empty for unlimited).");
            var url = string.IsNullOrWhiteSpace(req.ServerUrl) ? server.DefaultUrl : req.ServerUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.Query) || uri.AbsolutePath != "/")
                return Validation.Problem("serverUrl", "Enter an https:// address with host and optional port only, e.g. https://zentriq.example.com:8444");
            if (server.Ca is null) return Results.Problem(statusCode: 503, title: "Agent CA is not provisioned. Run 'zentriqctl repair' on the server.");
            var defaults = await LoadSettingsAsync(ds, ct);
            var checkin = ValidCheckin(req.CheckinMinutes) ?? defaults.CheckinMinutes;
            var inventory = ValidInventory(req.InventoryMinutes) ?? defaults.InventoryMinutes;
            if (inventory < checkin) return Validation.Problem("inventoryMinutes", "Inventory cannot be sent more often than the agent checks in.");

            var id = Guid.NewGuid();
            var expires = DateTimeOffset.UtcNow.AddDays(days);
            string code = "";
            // 20 characters; the first 6 identify the code (unique), the rest is the secret. Retry on the rare id collision.
            for (var attempt = 0; ; attempt++)
            {
                code = AgentCodes.NewEnrollmentCode();
                await using var cmd = ds.CreateCommand("""
                    INSERT INTO zentriq.agent_enrollment_tokens (id, name, token_hash, server_url, max_uses, expires_at, created_by, code_protected, short_id, checkin_minutes, inventory_minutes)
                    VALUES (@id, @n, @h, @u, @m, @e, @by, @code, @sid, @ci, @inv)
                    """);
                cmd.Parameters.AddWithValue("id", id);
                cmd.Parameters.AddWithValue("n", req.Name.Trim());
                cmd.Parameters.AddWithValue("h", SHA256.HashData(Encoding.UTF8.GetBytes(code)));
                cmd.Parameters.AddWithValue("u", url);
                cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Integer) { Value = (object?)req.MaxUses ?? DBNull.Value });
                cmd.Parameters.AddWithValue("e", expires);
                cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
                // Kept encrypted so administrators can show the code again (Agents > Enrollment codes > Show code).
                cmd.Parameters.AddWithValue("code", protector.Protect(id, new Zentriq.Infrastructure.Secrets.CredentialSecret { Password = code }));
                cmd.Parameters.AddWithValue("sid", AgentCodes.EnrollmentId(code));
                cmd.Parameters.AddWithValue("ci", checkin);
                cmd.Parameters.AddWithValue("inv", inventory);
                try { await cmd.ExecuteNonQueryAsync(ct); break; }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && attempt < 5) { }
            }
            await rc.AuditAsync("agent.token_create", req.Name, detail: new { id, url, days, req.MaxUses, checkin, inventory });
            return Results.Ok(new
            {
                id, name = req.Name.Trim(), serverUrl = url, expiresAt = expires, maxUses = req.MaxUses, code = AgentCodes.Group(code),
                checkinMinutes = checkin, inventoryMinutes = inventory,
                installCommand = InstallCommand(url, AgentCodes.Group(code), checkin, inventory),
                package = PackageInfo(env),
            });
        }).RequireAuthorization(Permissions.AgentsManage);

        g.MapGet("/enrollment-tokens/{id:guid}/code", async (Guid id, NpgsqlDataSource ds, RequestContext rc, IWebHostEnvironment env, Zentriq.Infrastructure.Secrets.CredentialProtector protector, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT name, code_protected, revoked_at IS NULL AND expires_at > now() AND (max_uses IS NULL OR uses < max_uses), server_url, expires_at, max_uses, uses, checkin_minutes, inventory_minutes FROM zentriq.agent_enrollment_tokens WHERE id = @id");
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            var name = r.GetString(0);
            if (r.IsDBNull(1)) return Results.Problem(statusCode: 410, title: "This code was created by an earlier version and cannot be shown again. Create a new enrollment code.");
            if (!r.GetBoolean(2)) return Results.Problem(statusCode: 410, title: "This enrollment code is no longer active (expired, revoked or used up).");
            var code = protector.Unprotect(id, r.GetString(1)).Password!;
            var defaults = await LoadSettingsAsync(ds, ct);
            var ci = r.IsDBNull(7) ? defaults.CheckinMinutes : r.GetInt32(7);
            var inv = r.IsDBNull(8) ? defaults.InventoryMinutes : r.GetInt32(8);
            var shown = AgentCodes.IsShortEnrollmentCode(code) ? AgentCodes.Group(code) : code;
            var result = new
            {
                id, name, code = shown, serverUrl = r.GetString(3), expiresAt = r.GetFieldValue<DateTimeOffset>(4), maxUses = r.IsDBNull(5) ? (int?)null : r.GetInt32(5), uses = r.GetInt32(6),
                checkinMinutes = ci, inventoryMinutes = inv,
                installCommand = AgentCodes.IsShortEnrollmentCode(code) ? InstallCommand(r.GetString(3), shown, ci, inv) : $"msiexec /i ZentriqAgent-x64.msi /qn ENROLLMENTCODE=\"{code}\"",
                package = PackageInfo(env),
            };
            await r.DisposeAsync();
            await rc.AuditAsync("agent.token_viewed", name);
            return Results.Ok(result);
        }).RequireAuthorization(Permissions.AgentsManage);

        // Delete enrollment codes that can no longer be used (revoked, expired or used up); devices enrolled with them stay.
        g.MapDelete("/enrollment-tokens/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                DELETE FROM zentriq.agent_enrollment_tokens
                 WHERE id = @id AND (revoked_at IS NOT NULL OR expires_at <= now() OR (max_uses IS NOT NULL AND uses >= max_uses)) RETURNING name
                """);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is string name) { await rc.AuditAsync("agent.token_delete", name); return Results.NoContent(); }
            await using var ex = ds.CreateCommand("SELECT 1 FROM zentriq.agent_enrollment_tokens WHERE id = @id");
            ex.Parameters.AddWithValue("id", id);
            return await ex.ExecuteScalarAsync(ct) is null ? Results.NotFound() : Results.Problem(statusCode: 409, title: "This code can still be used. Revoke it first, then delete it.");
        }).RequireAuthorization(Permissions.AgentsManage);

        g.MapPost("/enrollment-tokens/delete-inactive", async (NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                DELETE FROM zentriq.agent_enrollment_tokens WHERE revoked_at IS NOT NULL OR expires_at <= now() OR (max_uses IS NOT NULL AND uses >= max_uses)
                """);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("agent.token_delete", $"{n} inactive code(s)");
            return Results.Ok(new { deleted = n });
        }).RequireAuthorization(Permissions.AgentsManage);

        g.MapPost("/enrollment-tokens/{id:guid}/revoke", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("UPDATE zentriq.agent_enrollment_tokens SET revoked_at = now() WHERE id = @id AND revoked_at IS NULL RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("agent.token_revoke", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.AgentsManage);

        g.MapGet("/package", (IWebHostEnvironment env, AgentServer server) =>
            Results.Ok(new { package = PackageInfo(env), agentPort = server.Port, defaultServerUrl = server.DefaultUrl, enrollmentAvailable = server.Ca is not null }))
            .RequireAuthorization(Permissions.AssetsView);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    public const string PackagePath = "downloads/ZentriqAgent-x64.msi";

    public sealed record UpdateRequest(Guid[]? AgentIds, bool? All);
    public sealed record ScheduleRequest(Guid[]? AgentIds, bool? All, int? CheckinMinutes, int? InventoryMinutes);

    public static string Minutes(int m) => m % 1440 == 0 ? $"{m / 1440} day{(m == 1440 ? "" : "s")}" : m % 60 == 0 ? $"{m / 60} hour{(m == 60 ? "" : "s")}" : $"{m} minutes";

    /// <summary>Version of the agent package this server serves (from ZentriqAgent-x64.version), or null.</summary>
    internal static string? PackageVersion(IWebHostEnvironment env)
    {
        var file = env.WebRootFileProvider.GetFileInfo(PackagePath);
        if (!file.Exists || file.PhysicalPath is null) return null;
        var vfile = Path.ChangeExtension(file.PhysicalPath, ".version");
        return File.Exists(vfile) ? File.ReadAllText(vfile).Trim() : null;
    }

    /// <summary>True when <paramref name="version"/> is older than <paramref name="latest"/> (1.1.1 = 1.1.1.0).</summary>
    internal static bool IsOlder(string? version, string? latest) => AgentProtocol.IsOlderVersion(version, latest);

    private static readonly object HashLock = new();
    private static (string Path, DateTime Written, long Length, string Sha)? _hash;

    internal static string PackageSha256(string path)
    {
        var info = new FileInfo(path);
        lock (HashLock)
        {
            if (_hash is { } h && h.Path == path && h.Written == info.LastWriteTimeUtc && h.Length == info.Length) return h.Sha;
            using var s = File.OpenRead(path);
            var sha = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
            _hash = (path, info.LastWriteTimeUtc, info.Length, sha);
            return sha;
        }
    }

    /// <summary>
    /// Agent-update bookkeeping at check-in: an update is done when the agent reports the target version; one that
    /// was started but the agent came back with its old version more than 15 minutes later has failed.
    /// </summary>
    private static async Task CompleteUpdatesAsync(NpgsqlDataSource ds, Guid agentId, string? version, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version)) return;
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.asset_actions
               SET status = CASE WHEN target = @v THEN 'succeeded' ELSE 'failed' END,
                   result_state = CASE WHEN target = @v THEN 'updated' ELSE NULL END,
                   detail = CASE WHEN target = @v THEN 'Updated to ' || @v || '.'
                                 ELSE 'The agent still reports ' || @v || ' after the update was started. See C:\ProgramData\Zentriq\Agent\Logs\update-' || target || '.log on the device.' END,
                   completed_at = now()
             WHERE agent_id = @a AND kind = 'agent-update'
               AND (target = @v AND status IN ('queued','sent','running')
                    OR status = 'running' AND sent_at < now() - interval '15 minutes')
            """);
        cmd.Parameters.AddWithValue("a", agentId);
        cmd.Parameters.AddWithValue("v", version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static object? PackageInfo(IWebHostEnvironment env)
    {
        var file = env.WebRootFileProvider.GetFileInfo(PackagePath);
        if (!file.Exists || file.PhysicalPath is null) return null;
        string? version = null;
        var vfile = Path.ChangeExtension(file.PhysicalPath, ".version");
        if (File.Exists(vfile)) version = File.ReadAllText(vfile).Trim();
        return new { url = "/" + PackagePath, size = file.Length, version };
    }

    public static string BuildCode(string url, string secret, string[] pins) =>
        AgentProtocol.FormatCode(new AgentProtocol.EnrollmentCode { Url = url, Token = secret, Pins = [.. pins] });

    private static string[] Pins(AgentServer server) => server.ServerPin is { } p ? [p] : [];

    private static readonly Guid UninstallKeyAad = new("2b7f0c44-8a1e-4d6b-9c55-6e3f1a0d7b19");
    internal static string ProtectKey(Zentriq.Infrastructure.Secrets.CredentialProtector protector, Guid agentId, byte[] key) =>
        protector.Protect(UninstallKeyAad, new Zentriq.Infrastructure.Secrets.CredentialSecret { Password = agentId.ToString("N") + ":" + Convert.ToBase64String(key) });
    internal static byte[]? UnprotectKey(Zentriq.Infrastructure.Secrets.CredentialProtector protector, Guid agentId, string value)
    {
        try
        {
            var p = protector.Unprotect(UninstallKeyAad, value).Password ?? "";
            var parts = p.Split(':', 2);
            return parts.Length == 2 && parts[0] == agentId.ToString("N") ? Convert.FromBase64String(parts[1]) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException) { return null; }
    }

    private static readonly Guid UninstallCodeAad = new("6c1d9e2a-47b3-4f0e-8a6d-2f5b9c3e7a41");
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    /// <summary>The current uninstall code for all agents (only its hash goes to the agents).</summary>
    internal sealed record CodeForAll(string Hash, DateTimeOffset ExpiresAt, string CodeProtected, string? CreatedBy, DateTimeOffset CreatedAt);

    /// <summary>The current code, or null when there is none or it has expired.</summary>
    internal static async Task<CodeForAll?> LoadCodeForAllAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'agents.uninstall_code'");
        if (await cmd.ExecuteScalarAsync(ct) is not string json) return null;
        try
        {
            var c = JsonSerializer.Deserialize<CodeForAll>(json, JsonWeb);
            return c is not null && c.ExpiresAt > DateTimeOffset.UtcNow ? c : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? ShowCode(Zentriq.Infrastructure.Secrets.CredentialProtector protector, CodeForAll c)
    {
        try { return AgentCodes.Group(protector.Unprotect(UninstallCodeAad, c.CodeProtected).Password ?? "", 4); }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException) { return null; }
    }

    /// <summary>What an agent receives: the hash and expiry (null when there is no code or protection is off).</summary>
    private static object? CodeForAgents(CodeForAll? c, bool protection) => protection && c is not null ? new { hash = c.Hash, expiresAt = c.ExpiresAt } : null;

    /// <summary>The device's IPv4 address: its inventoried address, else the address it connects from (IPv4-mapped shown as IPv4).</summary>
    internal static string? Ipv4(string? assetIp, string? lastIp)
    {
        static string? V4(string? ip) =>
            System.Net.IPAddress.TryParse(ip, out var a)
                ? a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? a.ToString() : a.IsIPv4MappedToIPv6 ? a.MapToIPv4().ToString() : null
                : null;
        return V4(assetIp) ?? V4(lastIp) ?? (System.Net.IPAddress.TryParse(lastIp, out var x) && System.Net.IPAddress.IsLoopback(x) ? "127.0.0.1" : lastIp);
    }

    private static System.Net.IPAddress? ClientIp(HttpContext http) =>
        http.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } m ? m.MapToIPv4() : http.Connection.RemoteIpAddress;

    internal static string RequestKey(Guid agentId) => "agent.collect." + agentId.ToString("N");

    private const string PendingSql = """
        EXISTS (SELECT 1 FROM zentriq.asset_actions x WHERE x.agent_id = @id AND x.channel = 'agent' AND x.status = 'queued'
                   AND x.requested_at > now() - interval '24 hours')
        OR EXISTS (SELECT 1 FROM zentriq.system_settings s WHERE s.key = @rk)
        """;

    /// <summary>
    /// A presence request arrived: the agent is online now; its last contact is now. If it had been offline, the gap
    /// since its previous contact is recorded as its latest outage. Returns whether work is waiting for it.
    /// </summary>
    private static async Task<(bool Wake, bool WasOffline)> PresenceSeenAsync(NpgsqlDataSource ds, Guid agentId, System.Net.IPAddress? ip, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"""
            UPDATE zentriq.agent_registrations r
               SET last_outage_from = CASE WHEN r.online_until < now() AND r.last_checkin_at IS NOT NULL THEN r.last_checkin_at ELSE r.last_outage_from END,
                   last_outage_to   = CASE WHEN r.online_until < now() AND r.last_checkin_at IS NOT NULL THEN now() ELSE r.last_outage_to END,
                   online_until = now() + make_interval(secs => @grace), last_checkin_at = now(), last_ip = COALESCE(@ip, r.last_ip)
              FROM (SELECT online_until AS before FROM zentriq.agent_registrations WHERE id = @id) o
             WHERE r.id = @id
            RETURNING ({PendingSql}), COALESCE(o.before < now(), true)
            """);
        cmd.Parameters.AddWithValue("id", agentId);
        cmd.Parameters.AddWithValue("rk", RequestKey(agentId));
        cmd.Parameters.AddWithValue("grace", AgentPresence.OnlineGrace.TotalSeconds);
        cmd.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = (object?)ip ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetBoolean(0), r.GetBoolean(1)) : (false, false);
    }

    private static async Task<bool> PendingWorkAsync(NpgsqlDataSource ds, Guid agentId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"SELECT {PendingSql}");
        cmd.Parameters.AddWithValue("id", agentId);
        cmd.Parameters.AddWithValue("rk", RequestKey(agentId));
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<bool> RequestedAsync(NpgsqlDataSource ds, Guid agentId, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT 1 FROM zentriq.system_settings WHERE key = @k");
        cmd.Parameters.AddWithValue("k", RequestKey(agentId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<(AgentIdentity?, IResult?)> AuthenticateAsync(HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct)
    {
        var cert = http.Connection.ClientCertificate ?? await http.Connection.GetClientCertificateAsync(ct);
        if (cert is null) return (null, Results.Problem(statusCode: 401, title: "A client certificate is required."));
        if (server.Ca is null) return (null, Results.Problem(statusCode: 503, title: "Agent CA is not provisioned."));
        var id = AgentCertificateAuthority.Validate(server.Ca, cert, out var err);
        if (id is null) return (null, Results.Problem(statusCode: 401, title: "Client certificate rejected: " + err));
        await using var cmd = ds.CreateCommand("SELECT status, cert_thumbprint, asset_id FROM zentriq.agent_registrations WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return (null, Results.Problem(statusCode: 401, title: "Unknown agent. Re-enroll it."));
        var status = r.GetString(0);
        if (status == "revoked") return (null, Results.Problem(statusCode: 403, title: "This agent has been revoked."));
        if (status == "replaced") return (null, Results.Problem(statusCode: 403, title: "This agent has been revoked: the device was enrolled again and uses its newer registration."));
        // Only the latest issued certificate is valid (a renewed certificate replaces the old one).
        if (r.IsDBNull(1) || !string.Equals(r.GetString(1), cert.Thumbprint, StringComparison.OrdinalIgnoreCase))
            return (null, Results.Problem(statusCode: 401, title: "This certificate has been superseded."));
        return (new AgentIdentity(id.Value, r.IsDBNull(2) ? null : r.GetGuid(2), cert.Thumbprint), null);
    }

    private static NpgsqlParameter Text(string name, string? v) => new(name, NpgsqlDbType.Text) { Value = (object?)v ?? DBNull.Value };
    private static string? Trunc(string? s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > n ? s.Trim()[..n] : s.Trim();
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Read-only stream wrapper that fails once more than <c>max</c> bytes are read (gzip bomb guard).</summary>
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Count(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));
        private int Count(int n)
        {
            _read += n;
            if (_read > max) throw new InvalidDataException($"Inventory exceeds {max / (1024 * 1024)} MB.");
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
