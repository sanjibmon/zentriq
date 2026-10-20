using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Data;
using Zentriq.Infrastructure.Jobs;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Device actions from Asset Inventory: collect inventory now (one, selected or all assets) and start / stop /
/// restart Windows services. Services are controlled by the device's Zentriq Agent when it has one (no credential
/// needed), otherwise agentlessly by the worker over WinRM/DCOM with a stored credential or one typed for this action.
/// </summary>
public static class AssetActionEndpoints
{
    public sealed record CollectBody(Guid[]? AssetIds, bool? All);
    public sealed record ServiceActionBody(Guid? CredentialId, string? Username, string? Password);
    public sealed record UninstallBody(string Name, string? Version, Guid[]? AssetIds, bool? All);

    /// <summary>Stopping these would cut the device off from management or crash Windows.</summary>
    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "ZentriqAgent", "ZentriqApi", "ZentriqWorker", "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "Winmgmt", "WinRM",
        "BrokerInfrastructure", "SystemEventsBroker", "Power", "PlugPlay", "gpsvc", "CryptSvc", "Netlogon", "EventLog", "mpssvc", "BFE",
    };

    public static void MapAssetActionEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/assets").WithTags("Assets").RequireModule(Modules.Inventory);

        // Collect inventory now: agents are asked at their next check-in; other assets are re-scanned agentlessly
        // with every stored inventory credential (each credential is still limited to its allowed networks).
        g.MapPost("/collect", async (CollectBody body, ZentriqDbContext db, NpgsqlDataSource ds, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            var ids = body.AssetIds?.Distinct().Take(20000).ToArray() ?? [];
            if (ids.Length == 0 && body.All != true) return Validation.Problem("assetIds", "Select assets, or collect for all assets.");
            await using var q = ds.CreateCommand("""
                SELECT a.id, host(a.primary_ip), a.hostname,
                       (SELECT r.id FROM zentriq.agent_registrations r WHERE r.asset_id = a.id AND r.status = 'active' ORDER BY r.last_checkin_at DESC NULLS LAST LIMIT 1)
                  FROM zentriq.assets a
                 WHERE a.status = 'active' AND (@all OR a.id = ANY(@ids))
                """);
            q.Parameters.AddWithValue("all", body.All == true);
            q.Parameters.AddWithValue("ids", ids);
            var agents = new List<Guid>(); var targets = new List<string>(); var skipped = 0;
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    if (!r.IsDBNull(3)) agents.Add(r.GetGuid(3));
                    else if (!r.IsDBNull(1)) targets.Add(r.GetString(1));
                    else skipped++;
                }
            foreach (var agentId in agents)
            {
                await using var cmd = ds.CreateCommand("""
                    INSERT INTO zentriq.system_settings (key, value) VALUES (@k, to_jsonb(now()))
                    ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = now()
                    """);
                cmd.Parameters.AddWithValue("k", AgentEndpoints.RequestKey(agentId));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            Guid? scanId = null; string? scanNote = null;
            if (targets.Count > 0)
            {
                var credIds = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM zentriq.credentials WHERE kind <> 'ldap_bind' ORDER BY name LIMIT 10").ToListAsync(ct);
                if (credIds.Count == 0) scanNote = $"{targets.Count} asset(s) without an agent were not collected: add a scan credential (Discovery › Credentials) or install the agent.";
                else
                {
                    var req = new DiscoveryEndpoints.ScanRequest($"Collect inventory ({targets.Count} asset{(targets.Count == 1 ? "" : "s")})", "list",
                        string.Join('\n', targets.Distinct()), null, null, null, null, credIds.ToArray());
                    var (scan, error) = await DiscoveryEndpoints.CreateScanAsync(req, null, db, jobs, rc, ct);
                    if (error is not null) scanNote = $"{targets.Count} asset(s) without an agent could not be scanned: the addresses are outside the approved scan scopes or limits (Discovery › Safeguards).";
                    else scanId = scan!.Id;
                }
            }
            await rc.AuditAsync("asset.collect_requested", body.All == true ? "all assets" : $"{ids.Length} asset(s)", detail: new { agents = agents.Count, agentless = targets.Count, skipped, scanId });
            var parts = new List<string>();
            if (agents.Count > 0) parts.Add($"{agents.Count} agent(s) will send inventory at their next check-in");
            if (scanId is not null) parts.Add($"{targets.Count} asset(s) without an agent are being collected agentlessly (scan started)");
            if (skipped > 0) parts.Add($"{skipped} asset(s) have no address and were skipped");
            if (scanNote is not null) parts.Add(scanNote);
            return Results.Accepted(value: new { agents = agents.Count, agentless = targets.Count, skipped, scanId, message = parts.Count == 0 ? "Nothing to collect." : string.Join(". ", parts) + "." });
        }).RequireAuthorization(Permissions.ScansRun);

        // What a service action needs for this asset (decides whether the console prompts for a credential).
        g.MapGet("/{id:guid}/control", async (Guid id, NpgsqlDataSource ds, ZentriqDbContext db, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT a.os_family, r.id, r.last_checkin_at, COALESCE('service-control' = ANY(r.capabilities), false), r.agent_version, r.online_until
                  FROM zentriq.assets a
                  LEFT JOIN LATERAL (SELECT * FROM zentriq.agent_registrations x WHERE x.asset_id = a.id AND x.status = 'active'
                                     ORDER BY x.last_checkin_at DESC NULLS LAST LIMIT 1) r ON true
                 WHERE a.id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return Results.NotFound();
            var hasAgent = !r.IsDBNull(1);
            var capable = r.GetBoolean(3);
            var online = !r.IsDBNull(5) && r.GetFieldValue<DateTimeOffset>(5) > DateTimeOffset.UtcNow;
            var osFamily = r.IsDBNull(0) ? null : r.GetString(0);
            var agentVersion = r.IsDBNull(4) ? null : r.GetString(4);
            await r.DisposeAsync();
            var creds = await db.Database.SqlQuery<CredRow>($"SELECT id AS \"Id\", name AS \"Name\", username AS \"Username\" FROM zentriq.credentials WHERE kind IN ('windows_domain','windows_local') ORDER BY name").ToListAsync(ct);
            return Results.Ok(new
            {
                channel = capable ? "agent" : "agentless", hasAgent, agentCapable = capable, agentOnline = online, agentVersion, osFamily,
                needsCredential = !capable, credentials = creds, protectedServices = Protected.OrderBy(x => x),
                note = hasAgent && !capable ? "The agent on this device is too old for service control; update it to the latest agent MSI, or use a credential." : null,
            });
        }).RequireAuthorization(Permissions.AssetsControl);

        g.MapGet("/{id:guid}/actions", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT x.id, x.channel, x.kind, x.target, x.operation, x.status, x.detail, x.result_state, x.requested_at, x.completed_at, u.display_name
                  FROM zentriq.asset_actions x LEFT JOIN zentriq.users u ON u.id = x.requested_by
                 WHERE x.asset_id = @id ORDER BY x.requested_at DESC LIMIT 50
                """);
            cmd.Parameters.AddWithValue("id", id);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), channel = r.GetString(1), kind = r.GetString(2), target = r.GetString(3), operation = r.GetString(4),
                    status = r.GetString(5), detail = r.IsDBNull(6) ? null : r.GetString(6), resultState = r.IsDBNull(7) ? null : r.GetString(7),
                    requestedAt = r.GetFieldValue<DateTimeOffset>(8), completedAt = r.IsDBNull(9) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(9),
                    requestedBy = r.IsDBNull(10) ? null : r.GetString(10),
                });
            return Results.Ok(list);
        }).RequireAuthorization(Permissions.AssetsView);

        g.MapPost("/{id:guid}/services/{name}/{operation}", async (Guid id, string name, string operation, ServiceActionBody body, NpgsqlDataSource ds,
            CredentialProtector protector, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            if (operation is not ("start" or "stop" or "restart")) return Validation.Problem("operation", "Use start, stop or restart.");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.IndexOfAny(['\'', '"', '\\', '\0']) >= 0) return Validation.Problem("name", "Invalid service name.");
            if (Protected.Contains(name) && operation != "start")
                return Results.Problem(statusCode: 409, title: $"'{name}' is protected: stopping it would cut the device off from management or destabilise Windows. Manage it on the device itself.");

            // The service must be one the inventory reported for this asset.
            await using (var chk = ds.CreateCommand("""
                SELECT a.hostname, EXISTS (SELECT 1 FROM jsonb_array_elements(COALESCE(d.document -> 'services', '[]')) s WHERE lower(s ->> 'name') = lower(@n)),
                       (SELECT r.id FROM zentriq.agent_registrations r WHERE r.asset_id = a.id AND r.status = 'active' AND 'service-control' = ANY(r.capabilities)
                         ORDER BY r.last_checkin_at DESC NULLS LAST LIMIT 1)
                  FROM zentriq.assets a LEFT JOIN zentriq.asset_inventory_documents d ON d.asset_id = a.id WHERE a.id = @id AND a.status = 'active'
                """))
            {
                chk.Parameters.AddWithValue("id", id);
                chk.Parameters.AddWithValue("n", name);
                await using var r = await chk.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                var hostname = r.IsDBNull(0) ? id.ToString() : r.GetString(0);
                if (!r.GetBoolean(1)) return Validation.Problem("name", $"The inventory of {hostname} has no service named '{name}'. Collect inventory again and retry.");
                Guid? agentId = r.IsDBNull(2) ? null : r.GetGuid(2);
                await r.DisposeAsync();

                var actionId = Guid.NewGuid();
                string channel; Guid? credId = null; string? username = null; string? secret = null;
                if (agentId is not null) channel = "agent";
                else
                {
                    channel = "agentless";
                    if (body.CredentialId is { } c) credId = c;
                    else if (!string.IsNullOrWhiteSpace(body.Username) && !string.IsNullOrEmpty(body.Password))
                    {
                        username = body.Username.Trim();
                        secret = protector.Protect(actionId, new CredentialSecret { Password = body.Password });
                    }
                    else return Results.Json(new { credentialRequired = true, title = "This device has no Zentriq Agent. Enter an administrator account for it (DOMAIN\\user or .\\user) or choose a stored credential." }, statusCode: 400);
                }
                await using (var ins = ds.CreateCommand("""
                    INSERT INTO zentriq.asset_actions (id, asset_id, agent_id, channel, kind, target, operation, credential_id, username, secret_protected, requested_by)
                    VALUES (@id, @a, @ag, @ch, 'service', @t, @op, @cr, @u, @s, @by)
                    """))
                {
                    ins.Parameters.AddWithValue("id", actionId);
                    ins.Parameters.AddWithValue("a", id);
                    ins.Parameters.Add(new NpgsqlParameter("ag", NpgsqlDbType.Uuid) { Value = (object?)agentId ?? DBNull.Value });
                    ins.Parameters.AddWithValue("ch", channel);
                    ins.Parameters.AddWithValue("t", name);
                    ins.Parameters.AddWithValue("op", operation);
                    ins.Parameters.Add(new NpgsqlParameter("cr", NpgsqlDbType.Uuid) { Value = (object?)credId ?? DBNull.Value });
                    ins.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = (object?)username ?? DBNull.Value });
                    ins.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)secret ?? DBNull.Value });
                    ins.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
                    await ins.ExecuteNonQueryAsync(ct);
                }
                if (channel == "agentless")
                {
                    var jobId = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.AssetAction, new { actionId }, MaxAttempts: 1, TimeoutSeconds: 300, CreatedBy: rc.UserId), ct: ct);
                    await using var upd = ds.CreateCommand("UPDATE zentriq.asset_actions SET job_id = @j WHERE id = @id");
                    upd.Parameters.AddWithValue("j", jobId);
                    upd.Parameters.AddWithValue("id", actionId);
                    await upd.ExecuteNonQueryAsync(ct);
                }
                await rc.AuditAsync($"asset.service_{operation}", $"{hostname}: {name}", detail: new { actionId, channel, credentialId = credId, username });
                return Results.Accepted(value: new
                {
                    id = actionId, channel,
                    message = channel == "agent" ? $"Sent to the Zentriq Agent on {hostname}; it runs at its next check-in (set by its check-in interval)." : $"Running on {hostname} over WinRM/DCOM…",
                });
            }
        }).RequireAuthorization(Permissions.AssetsControl);
    }

    private sealed record CredRow(Guid Id, string Name, string? Username);

    /// <summary>Software that must never be removed from the console (it would cut the device off from management).</summary>
    private static bool IsProtectedSoftware(string name) =>
        name.Contains("Zentriq", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft .NET Framework", StringComparison.OrdinalIgnoreCase);

    public static void MapActivityAndUninstallEndpoints(this RouteGroupBuilder api)
    {
        // Device activity log: every service start/stop/restart and software uninstall, newest first.
        api.MapGet("/activity", async (NpgsqlDataSource ds, string? kind, string? status, string? q, Guid? assetId, int? page, int? pageSize, CancellationToken ct) =>
        {
            var (p, ps) = Paging.Normalize(page, pageSize);
            const string where = """
                WHERE (@k::text IS NULL OR x.kind = @k) AND (@st::text IS NULL OR x.status = @st OR (@st = 'pending' AND x.status IN ('queued','sent','running')))
                  AND (@a::uuid IS NULL OR x.asset_id = @a)
                  AND (@q::text IS NULL OR x.target ILIKE @q OR a.hostname ILIKE @q OR x.detail ILIKE @q OR u.display_name ILIKE @q)
                """;
            void P(NpgsqlCommand c)
            {
                c.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = kind is "service" or "uninstall" or "agent-update" or "remote-support" ? kind : DBNull.Value });
                c.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Text) { Value = status is "succeeded" or "failed" or "pending" or "expired" ? status : DBNull.Value });
                c.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Uuid) { Value = (object?)assetId ?? DBNull.Value });
                c.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(q) ? DBNull.Value : $"%{q.Trim()}%" });
            }
            const string from = "FROM zentriq.asset_actions x JOIN zentriq.assets a ON a.id = x.asset_id LEFT JOIN zentriq.users u ON u.id = x.requested_by";
            await using var count = ds.CreateCommand($"SELECT count(*) {from} {where}");
            P(count);
            var total = (long)(await count.ExecuteScalarAsync(ct))!;
            await using var cmd = ds.CreateCommand($"""
                SELECT x.id, x.asset_id, a.hostname, host(a.primary_ip), x.channel, x.kind, x.target, x.argument, x.operation, x.status, x.detail,
                       x.result_state, x.requested_at, x.completed_at, u.display_name
                  {from} {where} ORDER BY x.requested_at DESC LIMIT @lim OFFSET @off
                """);
            P(cmd);
            cmd.Parameters.AddWithValue("lim", ps);
            cmd.Parameters.AddWithValue("off", (p - 1) * ps);
            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            while (await r.ReadAsync(ct))
                items.Add(new
                {
                    id = r.GetGuid(0), assetId = r.GetGuid(1), hostname = S(2), ip = S(3), channel = r.GetString(4), kind = r.GetString(5), target = r.GetString(6),
                    argument = S(7), operation = r.GetString(8), status = r.GetString(9), detail = S(10), resultState = S(11),
                    requestedAt = r.GetFieldValue<DateTimeOffset>(12), completedAt = r.IsDBNull(13) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(13), requestedBy = S(14),
                });
            return Results.Ok(new { items, total, page = p, pageSize = ps });
        }).WithTags("Assets").RequireModule(Modules.Inventory).RequireAuthorization(Permissions.AssetsView);

        // Uninstall one application from selected devices, or from every device that has it. Runs through the agent only.
        api.MapPost("/software/uninstall", async (UninstallBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var name = body.Name?.Trim() ?? "";
            if (name.Length is 0 or > 300) return Validation.Problem("name", "Choose the software to uninstall.");
            if (IsProtectedSoftware(name)) return Results.Problem(statusCode: 409, title: $"'{name}' is needed to manage the device and cannot be uninstalled from the console.");
            var ids = body.AssetIds?.Distinct().Take(20000).ToArray() ?? [];
            if (ids.Length == 0 && body.All != true) return Validation.Problem("assetIds", "Select devices, or uninstall from all devices.");
            await using var q = ds.CreateCommand("""
                SELECT a.id, a.hostname, host(a.primary_ip),
                       (SELECT r.id FROM zentriq.agent_registrations r WHERE r.asset_id = a.id AND r.status = 'active' AND 'software-uninstall' = ANY(r.capabilities)
                         ORDER BY r.last_checkin_at DESC NULLS LAST LIMIT 1),
                       EXISTS (SELECT 1 FROM zentriq.agent_registrations r WHERE r.asset_id = a.id AND r.status = 'active')
                  FROM zentriq.assets a
                 WHERE a.status = 'active' AND (@all OR a.id = ANY(@ids))
                   AND EXISTS (SELECT 1 FROM zentriq.software_inventory s WHERE s.asset_id = a.id AND lower(s.name) = lower(@n) AND (@v::text IS NULL OR s.version = @v))
                """);
            q.Parameters.AddWithValue("all", body.All == true);
            q.Parameters.AddWithValue("ids", ids);
            q.Parameters.AddWithValue("n", name);
            q.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(body.Version) ? DBNull.Value : body.Version.Trim() });
            var targets = new List<(Guid Asset, Guid Agent, string Host)>();
            var noAgent = new List<string>(); var oldAgent = new List<string>();
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                {
                    var host = r.IsDBNull(1) ? (r.IsDBNull(2) ? r.GetGuid(0).ToString() : r.GetString(2)) : r.GetString(1);
                    if (!r.IsDBNull(3)) targets.Add((r.GetGuid(0), r.GetGuid(3), host));
                    else if (r.GetBoolean(4)) oldAgent.Add(host);
                    else noAgent.Add(host);
                }
            foreach (var t in targets)
            {
                await using var ins = ds.CreateCommand("""
                    INSERT INTO zentriq.asset_actions (asset_id, agent_id, channel, kind, target, argument, operation, requested_by)
                    VALUES (@a, @ag, 'agent', 'uninstall', @t, @arg, 'uninstall', @by)
                    """);
                ins.Parameters.AddWithValue("a", t.Asset);
                ins.Parameters.AddWithValue("ag", t.Agent);
                ins.Parameters.AddWithValue("t", name);
                ins.Parameters.Add(new NpgsqlParameter("arg", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(body.Version) ? DBNull.Value : body.Version.Trim() });
                ins.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
                await ins.ExecuteNonQueryAsync(ct);
            }
            await rc.AuditAsync("software.uninstall_requested", name, detail: new { version = body.Version, devices = targets.Select(t => t.Host), skippedNoAgent = noAgent, skippedOldAgent = oldAgent });
            var parts = new List<string>();
            if (targets.Count > 0) parts.Add($"Uninstall of {name} sent to {targets.Count} device(s); each agent runs it at its next check-in (set by its check-in interval). Progress is in the Activity log");
            if (noAgent.Count > 0) parts.Add($"{noAgent.Count} device(s) have no Zentriq Agent and were skipped ({string.Join(", ", noAgent.Take(5))}{(noAgent.Count > 5 ? "…" : "")})");
            if (oldAgent.Count > 0) parts.Add($"{oldAgent.Count} device(s) run an older agent without uninstall support; update the agent ({string.Join(", ", oldAgent.Take(5))}{(oldAgent.Count > 5 ? "…" : "")})");
            if (parts.Count == 0) parts.Add("No selected device has this software in its latest inventory");
            return Results.Accepted(value: new { sent = targets.Count, skippedNoAgent = noAgent.Count, skippedOldAgent = oldAgent.Count, message = string.Join(". ", parts) + "." });
        }).WithTags("Software").RequireModule(Modules.Inventory).RequireAuthorization(Permissions.SoftwareUninstall);
    }

    /// <summary>Records an action result (from the agent or the worker) and updates the service state in the stored inventory.</summary>
    public static async Task<bool> CompleteAsync(NpgsqlDataSource ds, Guid actionId, Guid? agentId, bool ok, string? state, string? message, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE zentriq.asset_actions SET status = @st, result_state = @rs, detail = @d, completed_at = CASE WHEN @st = 'running' THEN NULL ELSE now() END, secret_protected = NULL
             WHERE id = @id AND (@ag::uuid IS NULL OR agent_id = @ag) AND status IN ('queued','sent','running')
            RETURNING asset_id, target
            """);
        cmd.Parameters.AddWithValue("id", actionId);
        cmd.Parameters.Add(new NpgsqlParameter("ag", NpgsqlDbType.Uuid) { Value = (object?)agentId ?? DBNull.Value });
        // "started": a long-running action (agent update) whose outcome is confirmed later (next check-in).
        cmd.Parameters.AddWithValue("st", !ok ? "failed" : state == "started" ? "running" : "succeeded");
        cmd.Parameters.Add(new NpgsqlParameter("rs", NpgsqlDbType.Text) { Value = (object?)state ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = (object?)Zentriq.Application.Security.Redactor.Redact(message ?? (ok ? "Done." : "Failed.")) });
        Guid assetId; string target;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return false;
            assetId = r.GetGuid(0); target = r.GetString(1);
        }
        if (ok && state == "uninstalled")
        {
            await using var del = ds.CreateCommand("DELETE FROM zentriq.software_inventory WHERE asset_id = @a AND lower(name) = lower(@n)");
            del.Parameters.AddWithValue("a", assetId);
            del.Parameters.AddWithValue("n", target);
            await del.ExecuteNonQueryAsync(ct);
        }
        else if (state is not null && state is not ("started" or "current" or "updated"))
        {
            await using var doc = ds.CreateCommand("""
                UPDATE zentriq.asset_inventory_documents
                   SET document = jsonb_set(document, '{services}', (
                        SELECT COALESCE(jsonb_agg(CASE WHEN lower(s ->> 'name') = lower(@n) THEN s || jsonb_build_object('state', @st) ELSE s END ORDER BY o), '[]')
                          FROM jsonb_array_elements(document -> 'services') WITH ORDINALITY AS e(s, o)))
                 WHERE asset_id = @a AND jsonb_typeof(document -> 'services') = 'array'
                """);
            doc.Parameters.AddWithValue("a", assetId);
            doc.Parameters.AddWithValue("n", target);
            doc.Parameters.AddWithValue("st", state);
            await doc.ExecuteNonQueryAsync(ct);
        }
        return true;
    }
}
