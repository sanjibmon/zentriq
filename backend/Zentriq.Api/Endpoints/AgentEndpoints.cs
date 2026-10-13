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
    public const int DefaultCheckinSeconds = 120;   // short, so "Collect now" and service actions run within ~2 minutes
    public const int DefaultInventoryHours = 12;
    private const long MaxInventoryCompressed = 8L * 1024 * 1024;
    private const long MaxInventoryJson = 48L * 1024 * 1024;

    public sealed record EnrollRequest(string Token, string Csr, string? Hostname, string? OsName, string? AgentVersion, string? SmbiosUuid);
    public sealed record RenewRequest(string Csr);
    public sealed record CheckinRequest(string? AgentVersion, string? Hostname, string? OsName, string? LastError, string[]? Capabilities = null);
    public sealed record ActionResult(bool Ok, string? State, string? Message);

    private sealed record AgentIdentity(Guid Id, Guid? AssetId, string Thumbprint);

    public static void MapAgentEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/agent/v1").WithTags("Agent protocol");

        g.MapPost("/enroll", async (EnrollRequest req, HttpContext http, NpgsqlDataSource ds, AgentServer server, AuditWriter audit, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("Zentriq.Agents");
            var ca = server.Ca;
            if (ca is null) return Results.Problem(statusCode: 503, title: "Agent enrollment is not configured on this server. Run 'zentriqctl repair'.");
            if (string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.Csr)) return Results.Problem(statusCode: 400, title: "Token and certificate request are required.");
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(req.Token.Trim()));

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            Guid tokenId;
            await using (var cmd = new NpgsqlCommand("""
                SELECT id FROM zentriq.agent_enrollment_tokens
                 WHERE token_hash = @h AND revoked_at IS NULL AND expires_at > now() AND (max_uses IS NULL OR uses < max_uses)
                 FOR UPDATE
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("h", hash);
                if (await cmd.ExecuteScalarAsync(ct) is not Guid t)
                {
                    log.LogWarning("Agent enrollment rejected from {Ip}: invalid, expired, revoked or used-up token", http.Connection.RemoteIpAddress);
                    return Results.Problem(statusCode: 403, title: "The enrollment code is invalid, expired, revoked or has reached its use limit.");
                }
                tokenId = t;
            }

            var agentId = Guid.NewGuid();
            X509Certificate2 cert;
            try { cert = AgentCertificateAuthority.Issue(ca, Convert.FromBase64String(req.Csr), agentId); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { return Results.Problem(statusCode: 400, title: ex.Message); }
            using (cert)
            {
                await using (var ins = new NpgsqlCommand("""
                    INSERT INTO zentriq.agent_registrations (id, agent_version, cert_thumbprint, cert_serial, cert_not_after, status, hostname, os_name, last_ip, token_id, enrolled_at, last_checkin_at)
                    VALUES (@id, @v, @tp, @sn, @na, 'active', @h, @os, @ip, @tok, now(), now())
                    """, conn, tx))
                {
                    ins.Parameters.AddWithValue("id", agentId);
                    ins.Parameters.AddWithValue("v", Trunc(req.AgentVersion, 40) ?? "unknown");
                    ins.Parameters.AddWithValue("tp", cert.Thumbprint);
                    ins.Parameters.AddWithValue("sn", cert.SerialNumber);
                    ins.Parameters.AddWithValue("na", cert.NotAfter.ToUniversalTime());
                    ins.Parameters.Add(Text("h", Trunc(req.Hostname, 255)));
                    ins.Parameters.Add(Text("os", Trunc(req.OsName, 200)));
                    ins.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = (object?)http.Connection.RemoteIpAddress ?? DBNull.Value });
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
                    checkinSeconds = DefaultCheckinSeconds,
                    inventoryHours = DefaultInventoryHours,
                    serverPins = Pins(server),
                });
            }
        }).RequireRateLimiting("agent-enroll").AllowAnonymous();

        g.MapPost("/checkin", async (CheckinRequest req, HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.agent_registrations
                   SET last_checkin_at = now(), last_ip = @ip, agent_version = COALESCE(@v, agent_version),
                       hostname = COALESCE(@h, hostname), os_name = COALESCE(@os, os_name), capabilities = COALESCE(@cap, capabilities)
                 WHERE id = @id
                RETURNING last_inventory_at, cert_not_after
                """);
            cmd.Parameters.AddWithValue("id", agent.Id);
            cmd.Parameters.Add(new NpgsqlParameter("ip", NpgsqlDbType.Inet) { Value = (object?)http.Connection.RemoteIpAddress ?? DBNull.Value });
            cmd.Parameters.Add(Text("v", Trunc(req.AgentVersion, 40)));
            cmd.Parameters.Add(Text("h", Trunc(req.Hostname, 255)));
            cmd.Parameters.Add(Text("os", Trunc(req.OsName, 200)));
            cmd.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (object?)req.Capabilities?.Take(20).Select(c => Trunc(c, 40)!).ToArray() ?? DBNull.Value });
            DateTimeOffset? lastInv = null, notAfter = null;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct))
                {
                    lastInv = r.IsDBNull(0) ? null : r.GetFieldValue<DateTimeOffset>(0);
                    notAfter = r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1);
                }
            var inventoryDue = lastInv is null || lastInv < DateTimeOffset.UtcNow.AddHours(-DefaultInventoryHours) || await RequestedAsync(ds, agent.Id, ct);
            // Pending device actions (service start/stop/restart). Actions older than 1 hour are not handed out any more.
            var actions = new List<object>();
            var caps = req.Capabilities ?? [];
            var kinds = new List<string>();
            if (caps.Contains("service-control")) kinds.Add("service");
            if (caps.Contains("software-uninstall")) kinds.Add("uninstall");
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
                checkinSeconds = DefaultCheckinSeconds,
                inventoryHours = DefaultInventoryHours,
                inventoryDue,
                renewCertificate = notAfter is { } na && na < DateTimeOffset.UtcNow.AddDays(30),
                serverPins = Pins(server),
            });
        }).AllowAnonymous();

        g.MapPost("/actions/{actionId:guid}/result", async (Guid actionId, ActionResult body, HttpContext http, NpgsqlDataSource ds, AgentServer server, CancellationToken ct) =>
        {
            var (agent, error) = await AuthenticateAsync(http, ds, server, ct);
            if (agent is null) return error!;
            var ok = await AssetActionEndpoints.CompleteAsync(ds, actionId, agent.Id, body.Ok, Trunc(body.State, 40), Trunc(body.Message, 2000), ct);
            return ok ? Results.NoContent() : Results.NotFound();
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

    public sealed record TokenRequest(string Name, int? ValidDays, int? MaxUses, string? ServerUrl);

    public static void MapAgentAdminEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/agents").WithTags("Agents");

        g.MapGet("", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT r.id, r.status, r.agent_version, r.hostname, r.os_name, host(r.last_ip), r.enrolled_at, r.last_checkin_at, r.last_inventory_at,
                       r.cert_not_after, r.asset_id, a.hostname, r.revoked_at, t.name
                  FROM zentriq.agent_registrations r
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
                var health = status == "revoked" ? "revoked"
                    : lastCheckin is null ? "never"
                    : lastCheckin > DateTimeOffset.UtcNow.AddSeconds(-DefaultCheckinSeconds * 3) ? "online"
                    : lastCheckin > DateTimeOffset.UtcNow.AddDays(-7) ? "stale" : "offline";
                list.Add(new
                {
                    id = r.GetGuid(0), status, health, version = r.GetString(2), hostname = S(3) ?? S(11), osName = S(4), lastIp = S(5),
                    enrolledAt = r.GetFieldValue<DateTimeOffset>(6), lastCheckinAt = lastCheckin, lastInventoryAt = D(8), certificateExpires = D(9),
                    assetId = r.IsDBNull(10) ? (Guid?)null : r.GetGuid(10), revokedAt = D(12), enrollmentCode = S(13),
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
            return Results.Accepted(value: new { message = "The agent will send inventory at its next check-in (within about 2 minutes)." });
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

            var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
            var id = Guid.NewGuid();
            var expires = DateTimeOffset.UtcNow.AddDays(days);
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.agent_enrollment_tokens (id, name, token_hash, server_url, max_uses, expires_at, created_by, code_protected)
                VALUES (@id, @n, @h, @u, @m, @e, @by, @code)
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", req.Name.Trim());
            cmd.Parameters.AddWithValue("h", SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
            cmd.Parameters.AddWithValue("u", url);
            cmd.Parameters.Add(new NpgsqlParameter("m", NpgsqlDbType.Integer) { Value = (object?)req.MaxUses ?? DBNull.Value });
            cmd.Parameters.AddWithValue("e", expires);
            cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
            var code = BuildCode(url, secret, Pins(server));
            // Kept encrypted so administrators can show the code again (Agents > Enrollment codes > Show code).
            cmd.Parameters.AddWithValue("code", protector.Protect(id, new Zentriq.Infrastructure.Secrets.CredentialSecret { Password = code }));
            await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("agent.token_create", req.Name, detail: new { id, url, days, req.MaxUses });
            return Results.Ok(new
            {
                id, name = req.Name.Trim(), serverUrl = url, expiresAt = expires, maxUses = req.MaxUses, code,
                installCommand = $"msiexec /i ZentriqAgent-x64.msi /qn ENROLLMENTCODE=\"{code}\"",
                package = PackageInfo(env),
            });
        }).RequireAuthorization(Permissions.AgentsManage);

        g.MapGet("/enrollment-tokens/{id:guid}/code", async (Guid id, NpgsqlDataSource ds, RequestContext rc, IWebHostEnvironment env, Zentriq.Infrastructure.Secrets.CredentialProtector protector, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT name, code_protected, revoked_at IS NULL AND expires_at > now() AND (max_uses IS NULL OR uses < max_uses), server_url, expires_at, max_uses, uses FROM zentriq.agent_enrollment_tokens WHERE id = @id");
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            var name = r.GetString(0);
            if (r.IsDBNull(1)) return Results.Problem(statusCode: 410, title: "This code was created by an earlier version and cannot be shown again. Create a new enrollment code.");
            if (!r.GetBoolean(2)) return Results.Problem(statusCode: 410, title: "This enrollment code is no longer active (expired, revoked or used up).");
            var code = protector.Unprotect(id, r.GetString(1)).Password!;
            var result = new
            {
                id, name, code, serverUrl = r.GetString(3), expiresAt = r.GetFieldValue<DateTimeOffset>(4), maxUses = r.IsDBNull(5) ? (int?)null : r.GetInt32(5), uses = r.GetInt32(6),
                installCommand = $"msiexec /i ZentriqAgent-x64.msi /qn ENROLLMENTCODE=\"{code}\"",
                package = PackageInfo(env),
            };
            await r.DisposeAsync();
            await rc.AuditAsync("agent.token_viewed", name);
            return Results.Ok(result);
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

    internal static string RequestKey(Guid agentId) => "agent.collect." + agentId.ToString("N");

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
