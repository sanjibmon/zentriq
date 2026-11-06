using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Deployment;
using Zentriq.Infrastructure.Patches;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Patch Management: missing operating system updates found by the Zentriq Agent (Windows Update Agent; apt, dnf, yum
/// or zypper on Linux), the patch state of every device, approvals, deployments and automatic deployment policies.
/// </summary>
public static class PatchEndpoints
{
    public sealed record ApprovalBody(string[]? Ids, string? Approval);
    public sealed record ScanBody(Guid[]? AssetIds, bool? All);
    public sealed record JobBody(string? Name, string[]? PatchIds, bool? AllMissing, string[]? Classifications, string? Reboot, Guid[]? AssetIds, Guid[]? GroupIds,
        Guid[]? DepartmentIds, bool? All, string? Platform, string? Note);
    public sealed record PolicyBody(string? Name, bool? Enabled, string? Platform, string[]? Classifications, string[]? Severities, bool? ApprovedOnly, int? DelayDays,
        Guid[]? AssetIds, Guid[]? GroupIds, Guid[]? DepartmentIds, bool? All, int[]? Days, string? StartTime, int? WindowHours, string? Reboot);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? G(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static DateTimeOffset? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    public static void MapPatchEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/patches").WithTags("Patch management").RequireModule(Modules.Patch).RequireAuthorization(Permissions.PatchView);

        g.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($"""
                WITH dev AS (
                    SELECT a.id, s.scanned_at, s.ok, s.reboot_pending,
                           (SELECT count(*) FROM zentriq.asset_patches ap WHERE ap.asset_id = a.id AND ap.state IN ('missing','failed')) AS missing,
                           (SELECT count(*) FROM zentriq.asset_patches ap JOIN zentriq.patches p ON p.id = ap.patch_id
                             WHERE ap.asset_id = a.id AND ap.state IN ('missing','failed') AND (p.classification IN ('critical','security') OR p.severity IN ('critical','important'))) AS urgent
                      FROM zentriq.assets a
                      JOIN zentriq.agent_registrations r ON r.asset_id = a.id AND r.status = 'active' AND '{PatchStore.Capability}' = ANY(r.capabilities)
                      LEFT JOIN zentriq.asset_patch_scans s ON s.asset_id = a.id
                     WHERE a.status = 'active')
                SELECT (SELECT count(DISTINCT id) FROM dev),
                       (SELECT count(DISTINCT id) FROM dev WHERE scanned_at IS NOT NULL AND ok),
                       (SELECT count(DISTINCT id) FROM dev WHERE scanned_at IS NOT NULL AND ok AND missing = 0),
                       (SELECT count(DISTINCT id) FROM dev WHERE urgent > 0),
                       (SELECT count(DISTINCT id) FROM dev WHERE reboot_pending),
                       (SELECT count(DISTINCT id) FROM dev WHERE scanned_at IS NOT NULL AND NOT ok),
                       (SELECT count(DISTINCT id) FROM dev WHERE scanned_at IS NULL),
                       (SELECT count(DISTINCT p.id) FROM zentriq.patches p JOIN zentriq.asset_patches ap ON ap.patch_id = p.id AND ap.state IN ('missing','failed')),
                       (SELECT count(DISTINCT p.id) FROM zentriq.patches p JOIN zentriq.asset_patches ap ON ap.patch_id = p.id AND ap.state IN ('missing','failed')
                         WHERE p.classification IN ('critical','security') OR p.severity IN ('critical','important')),
                       (SELECT count(*) FROM zentriq.asset_patches WHERE state = 'failed'),
                       (SELECT count(*) FROM zentriq.asset_actions WHERE kind = 'patch' AND operation = 'install' AND status IN ('queued','sent','running')),
                       (SELECT count(*) FROM zentriq.asset_patches WHERE state = 'installed' AND installed_at > now() - interval '30 days'),
                       (SELECT count(*) FROM zentriq.patch_policies WHERE enabled),
                       (SELECT count(*) FROM zentriq.agent_registrations WHERE status = 'active' AND NOT ('{PatchStore.Capability}' = ANY(capabilities)))
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var settings = await PatchStore.SettingsAsync(ds, ct);
            return Results.Ok(new
            {
                devices = r.GetInt64(0), scanned = r.GetInt64(1), upToDate = r.GetInt64(2), urgentDevices = r.GetInt64(3), rebootPending = r.GetInt64(4),
                scanFailed = r.GetInt64(5), notScanned = r.GetInt64(6), missingPatches = r.GetInt64(7), urgentPatches = r.GetInt64(8), failed = r.GetInt64(9),
                installing = r.GetInt64(10), installed30d = r.GetInt64(11), policies = r.GetInt64(12), olderAgents = r.GetInt64(13), settings,
            });
        });

        // ---- updates ------------------------------------------------------------------------------------------------
        g.MapGet("", async (NpgsqlDataSource ds, string? q, string? platform, string? classification, string? severity, string? approval, string? state, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT p.id, p.platform, p.kb, p.title, p.classification, p.severity, p.product, p.package, p.version, p.released_at, p.size_bytes, p.reboot, p.cves,
                       p.support_url, p.approval, u.email, p.approved_at, p.first_seen_at,
                       count(*) FILTER (WHERE ap.state = 'missing'), count(*) FILTER (WHERE ap.state = 'failed'),
                       count(*) FILTER (WHERE ap.state = 'pending_reboot'), count(*) FILTER (WHERE ap.state = 'installed')
                  FROM zentriq.patches p
                  LEFT JOIN zentriq.asset_patches ap ON ap.patch_id = p.id
                  LEFT JOIN zentriq.users u ON u.id = p.approved_by
                 WHERE (@q = '' OR p.title ILIKE '%' || @q || '%' OR p.kb = ltrim(upper(@q), 'KB') OR p.package ILIKE @q || '%' OR @q = ANY(p.cves))
                   AND (@pf = '' OR p.platform = @pf) AND (@cls = '' OR p.classification = @cls) AND (@sev = '' OR p.severity = @sev) AND (@ap = '' OR p.approval = @ap)
                 GROUP BY p.id, u.email
                HAVING (@st = '' OR (@st = 'missing' AND count(*) FILTER (WHERE ap.state IN ('missing','failed')) > 0)
                        OR (@st = 'installed' AND count(*) FILTER (WHERE ap.state IN ('missing','failed','pending_reboot')) = 0))
                 ORDER BY count(*) FILTER (WHERE ap.state IN ('missing','failed')) DESC,
                          CASE p.severity WHEN 'critical' THEN 0 WHEN 'important' THEN 1 WHEN 'moderate' THEN 2 WHEN 'low' THEN 3 ELSE 4 END,
                          p.released_at DESC NULLS LAST
                 LIMIT 2000
                """);
            cmd.Parameters.AddWithValue("q", (q ?? "").Trim());
            cmd.Parameters.AddWithValue("pf", platform ?? "");
            cmd.Parameters.AddWithValue("cls", classification ?? "");
            cmd.Parameters.AddWithValue("sev", severity ?? "");
            cmd.Parameters.AddWithValue("ap", approval ?? "");
            cmd.Parameters.AddWithValue("st", state ?? "");
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetString(0), platform = r.GetString(1), kb = S(r, 2), title = r.GetString(3), classification = r.GetString(4), severity = r.GetString(5), product = S(r, 6),
                    package = S(r, 7), version = S(r, 8), releasedAt = r.IsDBNull(9) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(9), size = r.IsDBNull(10) ? (long?)null : r.GetInt64(10),
                    reboot = r.IsDBNull(11) ? (bool?)null : r.GetBoolean(11), cves = r.GetFieldValue<string[]>(12), url = S(r, 13), approval = r.GetString(14), approvedBy = S(r, 15),
                    approvedAt = T(r, 16), firstSeenAt = T(r, 17), missing = r.GetInt64(18), failed = r.GetInt64(19), pendingReboot = r.GetInt64(20), installed = r.GetInt64(21),
                });
            return Results.Ok(list);
        });

        g.MapGet("/{id}/devices", async (string id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT a.id, COALESCE(a.hostname, host(a.primary_ip)), ap.state, ap.detected_at, ap.installed_at, ap.message
                  FROM zentriq.asset_patches ap JOIN zentriq.assets a ON a.id = ap.asset_id
                 WHERE ap.patch_id = @id ORDER BY CASE ap.state WHEN 'failed' THEN 0 WHEN 'missing' THEN 1 WHEN 'pending_reboot' THEN 2 ELSE 3 END, lower(a.hostname)
                 LIMIT 5000
                """);
            cmd.Parameters.AddWithValue("id", id);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new { assetId = r.GetGuid(0), device = S(r, 1), state = r.GetString(2), detectedAt = T(r, 3), installedAt = T(r, 4), message = S(r, 5) });
            return Results.Ok(list);
        });

        g.MapPost("/approval", async (ApprovalBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var ids = (body.Ids ?? []).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().Take(2000).ToArray();
            if (ids.Length == 0) return Validation.Problem("ids", "Choose the updates.");
            if (body.Approval is not ("approved" or "declined" or "none")) return Validation.Problem("approval", "Approve, decline or reset.");
            var n = await PatchStore.SetApprovalAsync(ds, ids, body.Approval, rc.UserId, ct);
            await rc.AuditAsync("patch." + (body.Approval == "none" ? "approval_reset" : body.Approval == "approved" ? "approve" : "decline"), ids.Length == 1 ? ids[0] : $"{ids.Length} updates", detail: new { ids = ids.Take(50) }, ct: ct);
            return Results.Ok(new { changed = n });
        }).RequireAuthorization(Permissions.PatchApprove);

        // ---- devices ------------------------------------------------------------------------------------------------
        g.MapGet("/devices", async (NpgsqlDataSource ds, string? q, string? state, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($"""
                SELECT a.id, COALESCE(a.hostname, host(a.primary_ip)), a.os_name, r.platform, COALESCE('{PatchStore.Capability}' = ANY(r.capabilities), false), r.last_checkin_at,
                       s.scanned_at, s.ok, s.message, s.source, COALESCE(s.reboot_pending, false),
                       count(ap.patch_id) FILTER (WHERE ap.state = 'missing'), count(ap.patch_id) FILTER (WHERE ap.state = 'failed'),
                       count(ap.patch_id) FILTER (WHERE ap.state IN ('missing','failed') AND (p.classification IN ('critical','security') OR p.severity IN ('critical','important'))),
                       count(ap.patch_id) FILTER (WHERE ap.state = 'pending_reboot'), count(ap.patch_id) FILTER (WHERE ap.state = 'installed'),
                       (SELECT x.status FROM zentriq.asset_actions x WHERE x.asset_id = a.id AND x.kind = 'patch' ORDER BY x.requested_at DESC LIMIT 1),
                       (SELECT x.operation FROM zentriq.asset_actions x WHERE x.asset_id = a.id AND x.kind = 'patch' ORDER BY x.requested_at DESC LIMIT 1)
                  FROM zentriq.assets a
                  JOIN LATERAL (SELECT x.platform, x.capabilities, x.last_checkin_at FROM zentriq.agent_registrations x WHERE x.asset_id = a.id AND x.status = 'active'
                                 ORDER BY x.last_checkin_at DESC NULLS LAST LIMIT 1) r ON true
                  LEFT JOIN zentriq.asset_patch_scans s ON s.asset_id = a.id
                  LEFT JOIN zentriq.asset_patches ap ON ap.asset_id = a.id
                  LEFT JOIN zentriq.patches p ON p.id = ap.patch_id
                 WHERE a.status = 'active' AND (@q = '' OR a.hostname ILIKE '%' || @q || '%')
                 GROUP BY a.id, r.platform, r.capabilities, r.last_checkin_at, s.asset_id
                HAVING (@st = '' OR (@st = 'missing' AND count(ap.patch_id) FILTER (WHERE ap.state IN ('missing','failed')) > 0)
                        OR (@st = 'urgent' AND count(ap.patch_id) FILTER (WHERE ap.state IN ('missing','failed') AND (p.classification IN ('critical','security') OR p.severity IN ('critical','important'))) > 0)
                        OR (@st = 'uptodate' AND s.ok AND count(ap.patch_id) FILTER (WHERE ap.state IN ('missing','failed')) = 0)
                        OR (@st = 'reboot' AND COALESCE(s.reboot_pending, false))
                        OR (@st = 'failed' AND (NOT COALESCE(s.ok, true) OR count(ap.patch_id) FILTER (WHERE ap.state = 'failed') > 0))
                        OR (@st = 'notscanned' AND s.asset_id IS NULL))
                 ORDER BY count(ap.patch_id) FILTER (WHERE ap.state IN ('missing','failed') AND (p.classification IN ('critical','security') OR p.severity IN ('critical','important'))) DESC,
                          count(ap.patch_id) FILTER (WHERE ap.state IN ('missing','failed')) DESC, lower(a.hostname)
                 LIMIT 5000
                """);
            cmd.Parameters.AddWithValue("q", (q ?? "").Trim());
            cmd.Parameters.AddWithValue("st", state ?? "");
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), device = S(r, 1), os = S(r, 2), platform = AppControlStore.Family(S(r, 3)), capable = r.GetBoolean(4), lastSeenAt = T(r, 5),
                    scannedAt = T(r, 6), scanOk = r.IsDBNull(7) ? (bool?)null : r.GetBoolean(7), scanMessage = S(r, 8), source = S(r, 9), rebootPending = r.GetBoolean(10),
                    missing = r.GetInt64(11), failed = r.GetInt64(12), urgent = r.GetInt64(13), pendingReboot = r.GetInt64(14), installed = r.GetInt64(15),
                    lastAction = S(r, 16), lastOperation = S(r, 17),
                });
            return Results.Ok(list);
        });

        g.MapGet("/devices/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            object? scan = null;
            await using (var sc = ds.CreateCommand("SELECT scanned_at, ok, message, source, reboot_pending, missing FROM zentriq.asset_patch_scans WHERE asset_id = @a"))
            {
                sc.Parameters.AddWithValue("a", id);
                await using var r = await sc.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) scan = new { scannedAt = T(r, 0), ok = r.GetBoolean(1), message = S(r, 2), source = S(r, 3), rebootPending = r.GetBoolean(4), missing = r.GetInt32(5) };
            }
            var patches = new List<object>();
            await using (var cmd = ds.CreateCommand("""
                SELECT p.id, p.kb, p.title, p.classification, p.severity, p.released_at, p.approval, p.reboot, ap.state, ap.detected_at, ap.installed_at, ap.message, p.cves, p.package, p.version
                  FROM zentriq.asset_patches ap JOIN zentriq.patches p ON p.id = ap.patch_id
                 WHERE ap.asset_id = @a
                 ORDER BY CASE ap.state WHEN 'failed' THEN 0 WHEN 'missing' THEN 1 WHEN 'pending_reboot' THEN 2 ELSE 3 END,
                          CASE p.severity WHEN 'critical' THEN 0 WHEN 'important' THEN 1 WHEN 'moderate' THEN 2 WHEN 'low' THEN 3 ELSE 4 END, COALESCE(ap.installed_at, ap.detected_at) DESC
                 LIMIT 3000
                """))
            {
                cmd.Parameters.AddWithValue("a", id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    patches.Add(new
                    {
                        id = r.GetString(0), kb = S(r, 1), title = r.GetString(2), classification = r.GetString(3), severity = r.GetString(4),
                        releasedAt = r.IsDBNull(5) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(5), approval = r.GetString(6), reboot = r.IsDBNull(7) ? (bool?)null : r.GetBoolean(7),
                        state = r.GetString(8), detectedAt = T(r, 9), installedAt = T(r, 10), message = S(r, 11), cves = r.GetFieldValue<string[]>(12), package = S(r, 13), version = S(r, 14),
                    });
            }
            var actions = new List<object>();
            await using (var ac = ds.CreateCommand("SELECT id, operation, status, detail, requested_at, completed_at FROM zentriq.asset_actions WHERE asset_id = @a AND kind = 'patch' ORDER BY requested_at DESC LIMIT 20"))
            {
                ac.Parameters.AddWithValue("a", id);
                await using var r = await ac.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    actions.Add(new { id = r.GetGuid(0), operation = r.GetString(1), status = r.GetString(2), detail = S(r, 3), requestedAt = T(r, 4), completedAt = T(r, 5) });
            }
            return Results.Ok(new { scan, patches, actions });
        });

        g.MapPost("/scan", async (ScanBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            Guid[] ids = body.AssetIds ?? [];
            if (body.All == true)
            {
                await using var all = ds.CreateCommand($"SELECT DISTINCT asset_id FROM zentriq.agent_registrations WHERE status = 'active' AND asset_id IS NOT NULL AND '{PatchStore.Capability}' = ANY(capabilities)");
                var list = new List<Guid>();
                await using var r = await all.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
                ids = [.. list];
            }
            if (ids.Length == 0) return Validation.Problem("assetIds", "Choose the devices.");
            var n = await PatchStore.RequestScanAsync(ds, ids, rc.UserId, ct);
            AgentPresence.Instance.Nudge();
            await rc.AuditAsync("patch.scan", body.All == true ? "all devices" : $"{ids.Length} device(s)", detail: new { requested = n }, ct: ct);
            return Results.Ok(new { requested = n, skipped = ids.Length - n });
        }).RequireAuthorization(Permissions.PatchDeploy);

        // ---- deployments --------------------------------------------------------------------------------------------
        g.MapPost("/jobs", async (JobBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var targets = new TargetSpec(body.AssetIds, body.GroupIds, body.DepartmentIds, body.All);
            if (targets.IsEmpty) return Validation.Problem("assetIds", "Choose the devices (or groups, departments, all devices).");
            var ids = body.PatchIds ?? [];
            if (body.AllMissing != true && ids.Length == 0) return Validation.Problem("patchIds", "Choose the updates, or install all missing updates.");
            if (body.Classifications is { } cl && cl.Any(c => !PatchStore.Classifications.Contains(c))) return Validation.Problem("classifications", "Unknown classification.");
            var platform = body.Platform is "windows" or "linux" ? body.Platform : null;
            if (platform is null && ids.Length > 0)
            {
                await using var pf = ds.CreateCommand("SELECT DISTINCT platform FROM zentriq.patches WHERE id = ANY(@ids)");
                pf.Parameters.AddWithValue("ids", ids.Select(x => x.ToLowerInvariant()).ToArray());
                var pfs = new List<string>();
                await using (var r = await pf.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) pfs.Add(r.GetString(0));
                if (pfs.Count > 1) return Validation.Problem("patchIds", "Choose Windows or Linux updates, not both, for one deployment.");
                platform = pfs.FirstOrDefault();
            }
            PatchJobResult res;
            try
            {
                res = await PatchStore.CreateJobAsync(ds, new PatchJobRequest(
                    string.IsNullOrWhiteSpace(body.Name) ? (body.AllMissing == true ? "Install missing updates" : ids.Length == 1 ? "Install 1 update" : $"Install {ids.Length} updates") : body.Name.Trim(),
                    ids, body.AllMissing == true, body.AllMissing == true ? new PatchFilter(body.Classifications, null, false, 0) : null,
                    body.Reboot == "never" ? "never" : "when-needed", targets, platform, body.Note, null), rc.UserId, ct);
            }
            catch (InvalidOperationException ex) { return Validation.Problem("assetIds", ex.Message); }
            AgentPresence.Instance.Nudge();
            await rc.AuditAsync("patch.deploy", body.Name ?? $"{ids.Length} update(s)", detail: new { jobId = res.Id, res.Sent, res.Skipped, allMissing = body.AllMissing == true, reboot = body.Reboot }, ct: ct);
            return Results.Created($"/api/v1/patches/jobs/{res.Id}", new { id = res.Id, sent = res.Sent, skipped = res.Skipped, nothing = res.Nothing });
        }).RequireAuthorization(Permissions.PatchDeploy);

        g.MapGet("/jobs", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT j.id, j.created_at, j.name, j.note, j.reboot, j.all_missing, cardinality(j.patch_ids), pol.name, u.email,
                       count(d.asset_id), count(*) FILTER (WHERE d.skipped IS NOT NULL),
                       count(*) FILTER (WHERE x.status IN ('queued','sent')), count(*) FILTER (WHERE x.status = 'running'),
                       count(*) FILTER (WHERE x.status = 'succeeded'), count(*) FILTER (WHERE x.status IN ('failed','expired')), COALESCE(sum(d.patches), 0)
                  FROM zentriq.patch_jobs j
                  LEFT JOIN zentriq.patch_policies pol ON pol.id = j.policy_id
                  LEFT JOIN zentriq.users u ON u.id = j.created_by
                  LEFT JOIN zentriq.patch_job_devices d ON d.job_id = j.id
                  LEFT JOIN zentriq.asset_actions x ON x.id = d.action_id
                 GROUP BY j.id, pol.name, u.email ORDER BY j.created_at DESC LIMIT 500
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), createdAt = r.GetFieldValue<DateTimeOffset>(1), name = r.GetString(2), note = S(r, 3), reboot = r.GetString(4), allMissing = r.GetBoolean(5),
                    chosen = r.GetInt32(6), policy = S(r, 7), createdBy = S(r, 8), devices = r.GetInt64(9), skipped = r.GetInt64(10), waiting = r.GetInt64(11), running = r.GetInt64(12),
                    succeeded = r.GetInt64(13), failed = r.GetInt64(14), patches = r.GetInt64(15),
                });
            return Results.Ok(list);
        });

        g.MapGet("/jobs/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT d.asset_id, COALESCE(a.hostname, host(a.primary_ip)), d.skipped, d.patches, x.id, x.status, x.detail, x.requested_at, x.sent_at, x.completed_at
                  FROM zentriq.patch_job_devices d JOIN zentriq.assets a ON a.id = d.asset_id LEFT JOIN zentriq.asset_actions x ON x.id = d.action_id
                 WHERE d.job_id = @id ORDER BY lower(a.hostname)
                """);
            cmd.Parameters.AddWithValue("id", id);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var skipped = S(r, 2);
                list.Add(new
                {
                    assetId = r.GetGuid(0), device = S(r, 1), patches = r.GetInt32(3), status = skipped is not null ? "skipped" : S(r, 5) ?? "unknown", detail = skipped ?? S(r, 6),
                    actionId = G(r, 4), requestedAt = T(r, 7), sentAt = T(r, 8), completedAt = T(r, 9),
                });
            }
            return Results.Ok(list);
        });

        g.MapPost("/jobs/{id:guid}/cancel", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.asset_actions x SET status = 'expired', completed_at = now(), detail = 'Cancelled by ' || @u || ' before the device started it.'
                  FROM zentriq.patch_job_devices d WHERE d.job_id = @id AND x.id = d.action_id AND x.status = 'queued'
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("u", rc.UserName);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("patch.deploy_cancel", id.ToString(), detail: new { cancelled = n }, ct: ct);
            return Results.Ok(new { cancelled = n });
        }).RequireAuthorization(Permissions.PatchDeploy);

        // ---- policies -----------------------------------------------------------------------------------------------
        g.MapGet("/policies", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT p.id, p.name, p.enabled, p.platform, p.classifications, p.severities, p.approved_only, p.delay_days, p.targets::text, p.days, p.start_time,
                       p.window_hours, p.reboot, p.last_run_at, p.created_at, p.updated_at,
                       (SELECT j.id FROM zentriq.patch_jobs j WHERE j.policy_id = p.id ORDER BY j.created_at DESC LIMIT 1)
                  FROM zentriq.patch_policies p ORDER BY lower(p.name)
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), enabled = r.GetBoolean(2), platform = r.GetString(3), classifications = r.GetFieldValue<string[]>(4),
                    severities = r.GetFieldValue<string[]>(5), approvedOnly = r.GetBoolean(6), delayDays = r.GetInt32(7), targets = JsonDocument.Parse(r.GetString(8)).RootElement,
                    days = r.GetFieldValue<int[]>(9), startTime = r.GetFieldValue<TimeOnly>(10).ToString("HH:mm"), windowHours = r.GetInt32(11), reboot = r.GetString(12),
                    lastRunAt = T(r, 13), createdAt = T(r, 14), updatedAt = T(r, 15), lastJobId = G(r, 16),
                });
            return Results.Ok(list);
        });

        g.MapPost("/policies", (PolicyBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => SavePolicyAsync(null, b, ds, rc, ct)).RequireAuthorization(Permissions.PatchApprove);
        g.MapPut("/policies/{id:guid}", (Guid id, PolicyBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => SavePolicyAsync(id, b, ds, rc, ct)).RequireAuthorization(Permissions.PatchApprove);

        g.MapDelete("/policies/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.patch_policies WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("patch.policy_delete", name, ct: ct);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.PatchApprove);

        // ---- settings -----------------------------------------------------------------------------------------------
        g.MapPut("/settings", async (PatchSettings body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (body.ScanHours is < 1 or > 168) return Validation.Problem("scanHours", "Scan every 1 to 168 hours.");
            if (body.RebootDelayMinutes is < 1 or > 240) return Validation.Problem("rebootDelayMinutes", "The restart notice is 1 to 240 minutes.");
            await PatchStore.SaveSettingsAsync(ds, body, ct);
            await rc.AuditAsync("patch.settings", null, detail: new { body.AutoScan, body.ScanHours, body.RebootDelayMinutes }, ct: ct);
            return Results.Ok(await PatchStore.SettingsAsync(ds, ct));
        }).RequireAuthorization(Permissions.PatchApprove);
    }

    private static async Task<IResult> SavePolicyAsync(Guid? id, PolicyBody b, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct)
    {
        var name = b.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120) return Validation.Problem("name", "Enter a name (at most 120 characters).");
        if (b.Platform is not ("windows" or "linux")) return Validation.Problem("platform", "Choose Windows or Linux.");
        var cls = (b.Classifications ?? []).Distinct().ToArray();
        if (cls.Length == 0) return Validation.Problem("classifications", "Choose at least one classification.");
        if (cls.Any(c => !PatchStore.Classifications.Contains(c))) return Validation.Problem("classifications", "Unknown classification.");
        var sev = (b.Severities ?? []).Distinct().ToArray();
        if (sev.Any(s => !PatchStore.Severities.Contains(s))) return Validation.Problem("severities", "Unknown severity.");
        var days = (b.Days ?? []).Distinct().OrderBy(x => x).ToArray();
        if (days.Length == 0 || days.Any(d => d is < 0 or > 6)) return Validation.Problem("days", "Choose the days of the maintenance window.");
        if (!TimeOnly.TryParse(b.StartTime ?? "", System.Globalization.CultureInfo.InvariantCulture, out var start)) return Validation.Problem("startTime", "Enter the start time (HH:mm).");
        if (b.WindowHours is not (>= 1 and <= 24)) return Validation.Problem("windowHours", "The window lasts 1 to 24 hours.");
        if (b.DelayDays is < 0 or > 365) return Validation.Problem("delayDays", "Wait 0 to 365 days after the release.");
        var targets = new TargetSpec(b.AssetIds, b.GroupIds, b.DepartmentIds, b.All);
        if (targets.IsEmpty) return Validation.Problem("assetIds", "Choose the devices (or groups, departments, all devices).");
        await using var cmd = ds.CreateCommand(id is null
            ? """
              INSERT INTO zentriq.patch_policies (name, enabled, platform, classifications, severities, approved_only, delay_days, targets, days, start_time, window_hours, reboot, created_by)
              VALUES (@n, @en, @pf, @cls, @sev, @ap, @dl, @t::jsonb, @days, @st, @wh, @rb, @by) RETURNING id
              """
            : """
              UPDATE zentriq.patch_policies SET name = @n, enabled = @en, platform = @pf, classifications = @cls, severities = @sev, approved_only = @ap, delay_days = @dl,
                     targets = @t::jsonb, days = @days, start_time = @st, window_hours = @wh, reboot = @rb, updated_at = now()
               WHERE id = @id RETURNING id
              """);
        if (id is not null) cmd.Parameters.AddWithValue("id", id.Value);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("en", b.Enabled != false);
        cmd.Parameters.AddWithValue("pf", b.Platform);
        cmd.Parameters.AddWithValue("cls", cls);
        cmd.Parameters.AddWithValue("sev", sev);
        cmd.Parameters.AddWithValue("ap", b.ApprovedOnly == true);
        cmd.Parameters.AddWithValue("dl", b.DelayDays ?? 0);
        cmd.Parameters.AddWithValue("t", targets.ToJson());
        cmd.Parameters.AddWithValue("days", days);
        cmd.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Time) { Value = start });
        cmd.Parameters.AddWithValue("wh", b.WindowHours!.Value);
        cmd.Parameters.AddWithValue("rb", b.Reboot == "never" ? "never" : "when-needed");
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Uuid) { Value = (object?)rc.UserId ?? DBNull.Value });
        try
        {
            if (await cmd.ExecuteScalarAsync(ct) is not Guid saved) return Results.NotFound();
            await rc.AuditAsync(id is null ? "patch.policy_create" : "patch.policy_update", name, detail: new { b.Platform, classifications = cls, days, startTime = b.StartTime, b.WindowHours, b.Reboot, enabled = b.Enabled != false }, ct: ct);
            return Results.Ok(new { id = saved });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A policy with this name exists."); }
    }
}
