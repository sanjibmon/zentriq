using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Deployment;
using Zentriq.Inventory;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Software Deployment: the organisation's approved software list (built from the standard catalogue, own entries and
/// current inventory), application control on devices with the Zentriq Agent (audit or block installations of software
/// that is not approved, with a message and "Request approval"), device groups (their own mode, WDAC on Windows),
/// requests, events, packages and push deployments to devices.
/// </summary>
public static class DeploymentEndpoints
{
    public sealed record SettingsBody(string? Mode, string? Message, bool? AllowRequests, bool? AutoRemove, bool? ProtectRemoteAgent);
    public sealed record CatalogBody(string? Name, string? Publisher, string? Category, string[]? Platforms, string[]? NamePatterns, string[]? PublisherPatterns, string? Website, string? Notes);
    public sealed record ApproveCatalogBody(string[]? Keys, string[]? Platforms, Guid? GroupId, string? Action);
    public sealed record RuleBody(string? Action, string? Label, string? Name, string? Publisher, string? VersionMin, string? VersionMax, string? Sha256,
        string? Platform, Guid? GroupId, Guid? AssetId, string? Note, DateTimeOffset? ExpiresAt);
    public sealed record TestBody(string? Name, string? Publisher, string? Signer, string? Version, string? Sha256, string? Platform, Guid? AssetId);
    public sealed record InventoryItem(string Name, string? Publisher, string Platform);
    public sealed record FromInventoryBody(InventoryItem[]? Items, Guid? GroupId, bool? WithPublisher);
    public sealed record GroupBody(string? Name, string? Description, Guid[]? DepartmentIds, Guid[]? LocationIds, Guid[]? AssetIds, string? Platform, string? AppMode, string? WdacMode);
    public sealed record DecideBody(string? Scope, Guid? GroupId, string? Match, string? Note);
    public sealed record PackageBody(string? UploadId, string? Name, string? Version, string? Publisher, string? Platform, string? Format, string? InstallArgs,
        string? UninstallArgs, string? CatalogKey, string? DetectName, string? Notes, bool? Approve, bool? Active);
    public sealed record JobBody(Guid PackageId, string? Operation, Guid[]? AssetIds, Guid[]? GroupIds, Guid[]? DepartmentIds, bool? All, string? Note);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const long MaxPackage = 8L * 1024 * 1024 * 1024;

    public static void MapDeploymentEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/deployment").WithTags("Software deployment").RequireModule(Modules.Deployment).RequireAuthorization(Permissions.DeployView);

        // ---- overview ---------------------------------------------------------------------------------------------
        g.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var s = await AppControlStore.LoadSettingsAsync(ds, ct);
            await using var cmd = ds.CreateCommand("""
                SELECT (SELECT count(*) FROM zentriq.app_control_rules WHERE action = 'allow'),
                       (SELECT count(*) FROM zentriq.app_control_rules WHERE action = 'block'),
                       (SELECT count(*) FROM zentriq.device_groups),
                       (SELECT count(*) FROM zentriq.deployment_packages WHERE active),
                       (SELECT count(*) FROM zentriq.app_control_requests WHERE status = 'pending'),
                       (SELECT count(*) FROM zentriq.app_control_events WHERE kind = 'blocked' AND occurred_at > now() - interval '24 hours'),
                       (SELECT count(*) FROM zentriq.app_control_events WHERE kind = 'audited' AND occurred_at > now() - interval '24 hours'),
                       (SELECT count(*) FROM zentriq.app_control_events WHERE kind IN ('detected','removed') AND occurred_at > now() - interval '7 days'),
                       (SELECT count(*) FROM zentriq.agent_registrations WHERE status = 'active'),
                       (SELECT count(*) FROM zentriq.agent_registrations WHERE status = 'active' AND 'app-control' = ANY(COALESCE(capabilities, ARRAY[]::text[]))),
                       (SELECT count(*) FROM zentriq.agent_registrations WHERE status = 'active' AND app_control_state = 'block'),
                       (SELECT count(*) FROM zentriq.agent_registrations WHERE status = 'active' AND app_control_state = 'audit'),
                       (SELECT count(*) FROM zentriq.agent_registrations WHERE status = 'active' AND wdac_state IN ('audit','enforce')),
                       (SELECT count(*) FROM zentriq.agent_registrations WHERE status = 'active' AND remote_protection = 'on'),
                       (SELECT count(*) FROM zentriq.asset_actions WHERE kind = 'deploy' AND status IN ('queued','sent','running')),
                       (SELECT count(*) FROM zentriq.asset_actions WHERE kind = 'deploy' AND status = 'failed' AND completed_at > now() - interval '7 days')
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            long L(int i) => r.GetInt64(i);
            return Results.Ok(new
            {
                settings = SettingsJson(s),
                approvedRules = L(0), blockedRules = L(1), groups = L(2), packages = L(3), pendingRequests = L(4),
                blocked24h = L(5), audited24h = L(6), detected7d = L(7),
                agents = L(8), capableAgents = L(9), blocking = L(10), auditing = L(11), wdac = L(12), remoteProtected = L(13),
                deploymentsOpen = L(14), deploymentsFailed7d = L(15),
                catalog = new { version = SoftwareCatalog.Standard.Version, entries = SoftwareCatalog.Standard.Entries.Count },
            });
        });

        g.MapGet("/settings", async (NpgsqlDataSource ds, CancellationToken ct) => Results.Ok(SettingsJson(await AppControlStore.LoadSettingsAsync(ds, ct))));

        g.MapPut("/settings", async (SettingsBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var s = await AppControlStore.LoadSettingsAsync(ds, ct);
            var old = s.Mode;
            if (body.Mode is not null)
            {
                if (!AppControl.Modes.Contains(body.Mode)) return Validation.Problem("mode", "Choose off, audit or block.");
                s.Mode = body.Mode;
            }
            if (body.Message is not null)
            {
                var m = body.Message.Trim();
                if (m.Length is < 10 or > 500) return Validation.Problem("message", "Enter the message users see (10 to 500 characters).");
                s.Message = m;
            }
            s.AllowRequests = body.AllowRequests ?? s.AllowRequests;
            s.AutoRemove = body.AutoRemove ?? s.AutoRemove;
            s.ProtectRemoteAgent = body.ProtectRemoteAgent ?? s.ProtectRemoteAgent;
            if (old == "off" && s.Mode != "off") s.EnabledAt = DateTimeOffset.UtcNow;
            s.Revision++;
            await AppControlStore.SaveSettingsAsync(ds, s, ct);
            AgentPresence.Instance.Nudge();
            await rc.AuditAsync("appcontrol.settings", s.Mode, detail: new { from = old, s.Mode, s.AllowRequests, s.AutoRemove, s.ProtectRemoteAgent });
            return Results.Ok(SettingsJson(s));
        }).RequireAuthorization(Permissions.DeployManage);

        // ---- standard catalogue and own entries ---------------------------------------------------------------------
        g.MapGet("/catalog", async (NpgsqlDataSource ds, string? q, string? category, string? platform, CancellationToken ct) =>
        {
            var entries = await AllCatalogAsync(ds, ct);
            var approved = await StringsAsync(ds, "SELECT DISTINCT catalog_key FROM zentriq.app_control_rules WHERE catalog_key IS NOT NULL AND action = 'allow'", ct);
            var blocked = await StringsAsync(ds, "SELECT DISTINCT catalog_key FROM zentriq.app_control_rules WHERE catalog_key IS NOT NULL AND action = 'block'", ct);
            var installs = await CatalogInstallsAsync(ds, entries, ct);
            var list = entries
                .Where(e => string.IsNullOrWhiteSpace(category) || string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrWhiteSpace(platform) || e.Platforms.Contains(platform))
                .Where(e => string.IsNullOrWhiteSpace(q) || e.Name.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase)
                            || (e.Publisher ?? "").Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) || e.Category.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Category).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new
                {
                    e.Key, e.Name, e.Publisher, e.Category, e.Platforms, e.NamePatterns, e.PublisherPatterns, e.Website, e.WindowsSilentArgs, e.DebPackage, e.RpmPackage,
                    e.BuiltIn, e.Id, e.Notes, approved = approved.Contains(e.Key), blocked = blocked.Contains(e.Key), devices = installs.GetValueOrDefault(e.Key),
                });
            return Results.Ok(new { version = SoftwareCatalog.Standard.Version, categories = SoftwareCatalog.Categories(entries), entries = list });
        });

        g.MapPost("/catalog", async (CatalogBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var name = body.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 120) return Validation.Problem("name", "Enter the software name (up to 120 characters).");
            var platforms = (body.Platforms ?? ["windows"]).Where(p => p is "windows" or "linux").Distinct().ToArray();
            if (platforms.Length == 0) return Validation.Problem("platforms", "Choose Windows, Linux or both.");
            var names = Clean(body.NamePatterns);
            if (names.Length == 0) names = [name + "*"];
            var key = "own-" + Slug(name);
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.software_catalog (key, name, publisher, category, platforms, name_patterns, publisher_patterns, website, notes, created_by)
                VALUES (@k, @n, @p, @c, @pl, @np, @pp, @w, @no, @by) ON CONFLICT (key) DO NOTHING RETURNING id
                """);
            cmd.Parameters.AddWithValue("k", key);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.Add(Text("p", Trim(body.Publisher, 200)));
            cmd.Parameters.AddWithValue("c", Trim(body.Category, 60) ?? "Other");
            cmd.Parameters.AddWithValue("pl", platforms);
            cmd.Parameters.AddWithValue("np", names);
            cmd.Parameters.AddWithValue("pp", Clean(body.PublisherPatterns));
            cmd.Parameters.Add(Text("w", Trim(body.Website, 300)));
            cmd.Parameters.Add(Text("no", Trim(body.Notes, 1000)));
            cmd.Parameters.Add(Uuid("by", rc.UserId));
            if (await cmd.ExecuteScalarAsync(ct) is not Guid id) return Validation.Problem("name", "Your catalogue already has software with this name.");
            await rc.AuditAsync("deployment.catalog_add", name);
            return Results.Created($"/api/v1/deployment/catalog/{id}", new { id, key });
        }).RequireAuthorization(Permissions.DeployManage);

        g.MapDelete("/catalog/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.software_catalog WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("deployment.catalog_delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeployManage);

        // Approve (or block) catalogue entries: one rule per platform.
        g.MapPost("/catalog/approve", async (ApproveCatalogBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var action = body.Action == "block" ? "block" : "allow";
            var entries = await AllCatalogAsync(ds, ct);
            var chosen = (body.Keys ?? []).Select(k => entries.FirstOrDefault(e => e.Key == k)).Where(e => e is not null).Select(e => e!).ToList();
            if (chosen.Count == 0) return Validation.Problem("keys", "Choose software from the catalogue.");
            if (body.GroupId is { } gid && !await ExistsAsync(ds, "SELECT 1 FROM zentriq.device_groups WHERE id = @id", gid, ct)) return Validation.Problem("groupId", "The device group does not exist.");
            var created = 0;
            foreach (var e in chosen)
                foreach (var (platform, name, publisher) in SoftwareCatalog.RulesFor(e, body.Platforms))
                {
                    await using var cmd = ds.CreateCommand("""
                        INSERT INTO zentriq.app_control_rules (action, label, name_pattern, publisher_pattern, platform, catalog_key, group_id, source, created_by)
                        SELECT @act, @l, @n, @p, @pf, @k, @g, 'catalog', @by
                         WHERE NOT EXISTS (SELECT 1 FROM zentriq.app_control_rules WHERE catalog_key = @k AND platform = @pf AND action = @act
                                             AND group_id IS NOT DISTINCT FROM @g AND asset_id IS NULL)
                        """);
                    cmd.Parameters.AddWithValue("act", action);
                    cmd.Parameters.AddWithValue("l", e.Name);
                    cmd.Parameters.AddWithValue("n", name);
                    cmd.Parameters.Add(Text("p", publisher));
                    cmd.Parameters.AddWithValue("pf", platform);
                    cmd.Parameters.AddWithValue("k", e.Key);
                    cmd.Parameters.Add(Uuid("g", body.GroupId));
                    cmd.Parameters.Add(Uuid("by", rc.UserId));
                    created += await cmd.ExecuteNonQueryAsync(ct);
                }
            if (created > 0) await ChangedAsync(ds, ct);
            await rc.AuditAsync(action == "block" ? "appcontrol.block_catalog" : "appcontrol.approve_catalog", string.Join(", ", chosen.Select(c => c.Name).Take(20)), detail: new { created, body.GroupId });
            return Results.Ok(new { created });
        }).RequireAuthorization(Permissions.DeployManage);

        // ---- approved and blocked software (rules) ------------------------------------------------------------------
        g.MapGet("/rules", async (NpgsqlDataSource ds, string? q, Guid? groupId, string? action, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT r.id, r.action, r.label, r.name_pattern, r.publisher_pattern, r.version_min, r.version_max, r.sha256, r.platform, r.catalog_key,
                       r.group_id, g.name, r.asset_id, a.hostname, r.source, r.note, r.expires_at, r.created_at, u.email
                  FROM zentriq.app_control_rules r
                  LEFT JOIN zentriq.device_groups g ON g.id = r.group_id
                  LEFT JOIN zentriq.assets a ON a.id = r.asset_id
                  LEFT JOIN zentriq.users u ON u.id = r.created_by
                 WHERE (@g::uuid IS NULL OR r.group_id = @g) AND (@act::text IS NULL OR r.action = @act)
                   AND (@q::text IS NULL OR r.label ILIKE @q OR r.name_pattern ILIKE @q OR r.publisher_pattern ILIKE @q OR r.sha256 ILIKE @q)
                 ORDER BY r.action DESC, lower(r.label), r.platform NULLS FIRST
                 LIMIT 5000
                """);
            cmd.Parameters.Add(Uuid("g", groupId));
            cmd.Parameters.Add(Text("act", action is "allow" or "block" ? action : null));
            cmd.Parameters.Add(Text("q", string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim() + "%"));
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), action = r.GetString(1), label = r.GetString(2), name = S(r, 3), publisher = S(r, 4), versionMin = S(r, 5), versionMax = S(r, 6),
                    sha256 = S(r, 7), platform = S(r, 8), catalogKey = S(r, 9), groupId = G(r, 10), group = S(r, 11), assetId = G(r, 12), device = S(r, 13),
                    source = r.GetString(14), note = S(r, 15), expiresAt = T(r, 16), createdAt = r.GetFieldValue<DateTimeOffset>(17), createdBy = S(r, 18),
                });
            return Results.Ok(list);
        });

        g.MapPost("/rules", async (RuleBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var (rule, problem) = CheckRule(body);
            if (problem is not null) return problem;
            var id = await InsertRuleAsync(ds, rule!, "manual", rc.UserId, ct);
            await ChangedAsync(ds, ct);
            await rc.AuditAsync(rule!.Action == "block" ? "appcontrol.block" : "appcontrol.approve", rule.Label, detail: rule);
            return Results.Created($"/api/v1/deployment/rules/{id}", new { id });
        }).RequireAuthorization(Permissions.DeployManage);

        g.MapPut("/rules/{id:guid}", async (Guid id, RuleBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var (rule, problem) = CheckRule(body);
            if (problem is not null) return problem;
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.app_control_rules SET action = @act, label = @l, name_pattern = @n, publisher_pattern = @p, version_min = @vmin, version_max = @vmax,
                       sha256 = @h, platform = @pf, group_id = @g, asset_id = @a, note = @no, expires_at = @x
                 WHERE id = @id
                """);
            RuleParams(cmd, rule!);
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await ChangedAsync(ds, ct);
            await rc.AuditAsync("appcontrol.rule_change", rule!.Label, detail: rule);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeployManage);

        g.MapDelete("/rules/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.app_control_rules WHERE id = @id RETURNING label");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string label) return Results.NotFound();
            await ChangedAsync(ds, ct);
            await rc.AuditAsync("appcontrol.rule_delete", label);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeployManage);

        // Would this software be allowed (on this device)?
        g.MapPost("/rules/test", async (TestBody body, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name)) return Validation.Problem("name", "Enter the software name.");
            var platform = body.Platform == "linux" ? "linux" : "windows";
            var policy = await AppControlStore.PolicyForAsync(ds, body.AssetId, platform == "linux" ? "linux-x64" : "windows-x64", ct);
            var d = AppControl.Evaluate(policy, new SoftwareCandidate
            {
                Name = body.Name.Trim(), Publisher = Trim(body.Publisher, 300), Signer = Trim(body.Signer, 300), Version = Trim(body.Version, 100),
                Sha256 = AppControl.NormalizeSha256(body.Sha256), Platform = platform,
            });
            return Results.Ok(new { approved = d.Approved, outcome = d.Outcome, reason = d.Reason, ruleId = d.RuleId, mode = policy.Mode,
                effect = d.Approved ? "installs" : policy.Mode == "block" ? "blocked" : policy.Mode == "audit" ? "reported" : "installs (application control is off)" });
        });

        // ---- software found on devices, for building the list ------------------------------------------------------
        g.MapGet("/inventory", async (NpgsqlDataSource ds, string? q, string? status, string? platform, CancellationToken ct) =>
        {
            var policyWin = await AppControlStore.PolicyForAsync(ds, null, "windows-x64", ct);
            var policyLinux = await AppControlStore.PolicyForAsync(ds, null, "linux-x64", ct);
            await using var cmd = ds.CreateCommand("""
                SELECT s.name, s.publisher, CASE WHEN a.os_family = 'linux' THEN 'linux' ELSE 'windows' END AS pf,
                       count(DISTINCT s.asset_id), array_agg(DISTINCT s.version) FILTER (WHERE s.version IS NOT NULL), max(s.installed_on)
                  FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id
                 WHERE a.status = 'active' AND a.os_family IN ('windows','linux')
                   AND (@q::text IS NULL OR s.name ILIKE @q OR s.publisher ILIKE @q)
                   AND (@pf::text IS NULL OR a.os_family = @pf)
                 GROUP BY s.name, s.publisher, pf
                 ORDER BY count(DISTINCT s.asset_id) DESC, lower(s.name)
                 LIMIT 20000
                """);
            cmd.Parameters.Add(Text("q", string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim() + "%"));
            cmd.Parameters.Add(Text("pf", platform is "windows" or "linux" ? platform : null));
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var pf = r.GetString(2);
                var versions = r.IsDBNull(4) ? [] : r.GetFieldValue<string[]>(4);
                var c = new SoftwareCandidate { Name = r.GetString(0), Publisher = S(r, 1), Version = versions.Length == 1 ? versions[0] : null, Platform = pf };
                var d = AppControl.Evaluate(pf == "linux" ? policyLinux : policyWin, c);
                if (status == "unapproved" && d.Approved) continue;
                if (status == "approved" && !d.Approved) continue;
                list.Add(new { name = c.Name, publisher = c.Publisher, platform = pf, devices = r.GetInt64(3), versions = versions.Take(10), lastInstalled = r.IsDBNull(5) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(5),
                    approved = d.Approved, outcome = d.Outcome, reason = d.Reason });
                if (list.Count >= 3000) break;
            }
            return Results.Ok(list);
        });

        g.MapPost("/rules/from-inventory", async (FromInventoryBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var items = (body.Items ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Name)).Take(5000).ToList();
            if (items.Count == 0) return Validation.Problem("items", "Choose the software to approve.");
            var created = 0;
            foreach (var i in items)
            {
                var pf = i.Platform == "linux" ? "linux" : "windows";
                var rule = new Rule("allow", i.Name.Trim(), Escape(i.Name.Trim()), body.WithPublisher == true && pf == "windows" && !string.IsNullOrWhiteSpace(i.Publisher) ? Escape(i.Publisher!.Trim()) : null,
                    null, null, null, pf, body.GroupId, null, "From inventory", null);
                await using var cmd = ds.CreateCommand("""
                    INSERT INTO zentriq.app_control_rules (action, label, name_pattern, publisher_pattern, platform, group_id, source, note, created_by)
                    SELECT 'allow', @l, @n, @p, @pf, @g, 'inventory', @no, @by
                     WHERE NOT EXISTS (SELECT 1 FROM zentriq.app_control_rules WHERE action = 'allow' AND lower(name_pattern) = lower(@n) AND platform = @pf
                                         AND group_id IS NOT DISTINCT FROM @g AND asset_id IS NULL AND publisher_pattern IS NOT DISTINCT FROM @p)
                    """);
                cmd.Parameters.AddWithValue("l", rule.Label);
                cmd.Parameters.AddWithValue("n", rule.Name!);
                cmd.Parameters.Add(Text("p", rule.Publisher));
                cmd.Parameters.AddWithValue("pf", pf);
                cmd.Parameters.Add(Uuid("g", body.GroupId));
                cmd.Parameters.AddWithValue("no", rule.Note!);
                cmd.Parameters.Add(Uuid("by", rc.UserId));
                created += await cmd.ExecuteNonQueryAsync(ct);
            }
            if (created > 0) await ChangedAsync(ds, ct);
            await rc.AuditAsync("appcontrol.approve_inventory", $"{created} item(s)", detail: new { created, body.GroupId });
            return Results.Ok(new { created });
        }).RequireAuthorization(Permissions.DeployManage);

        // ---- device groups --------------------------------------------------------------------------------------------
        g.MapGet("/groups", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($"""
                SELECT g.id, g.name, g.description, g.department_ids, g.location_ids, g.asset_ids, g.platform, g.app_mode, g.wdac_mode, g.created_at,
                       (SELECT count(*) FROM zentriq.assets a WHERE a.status = 'active' AND {AppControlStore.MemberSql}),
                       (SELECT count(*) FROM zentriq.app_control_rules r WHERE r.group_id = g.id),
                       COALESCE((SELECT array_agg(d.name ORDER BY d.name) FROM zentriq.departments d WHERE d.id = ANY(g.department_ids)), '{{}}'),
                       COALESCE((SELECT array_agg(l.name ORDER BY l.name) FROM zentriq.locations l WHERE l.id = ANY(g.location_ids)), '{{}}')
                  FROM zentriq.device_groups g ORDER BY lower(g.name)
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), description = S(r, 2), departmentIds = r.GetFieldValue<Guid[]>(3), locationIds = r.GetFieldValue<Guid[]>(4),
                    assetIds = r.GetFieldValue<Guid[]>(5), platform = S(r, 6), appMode = S(r, 7), wdacMode = r.GetString(8), createdAt = r.GetFieldValue<DateTimeOffset>(9),
                    devices = r.GetInt64(10), rules = r.GetInt64(11), departments = r.GetFieldValue<string[]>(12), locations = r.GetFieldValue<string[]>(13),
                });
            return Results.Ok(list);
        });

        g.MapPost("/groups", async (GroupBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (CheckGroup(body) is { } problem) return problem;
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.device_groups (name, description, department_ids, location_ids, asset_ids, platform, app_mode, wdac_mode, created_by)
                VALUES (@n, @d, @dep, @loc, @as, @pf, @m, @w, @by) RETURNING id
                """);
            GroupParams(cmd, body);
            cmd.Parameters.Add(Uuid("by", rc.UserId));
            try
            {
                var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
                await ChangedAsync(ds, ct);
                await rc.AuditAsync("deployment.group_create", body.Name!.Trim(), detail: body);
                return Results.Created($"/api/v1/deployment/groups/{id}", new { id });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "Another group has this name."); }
        }).RequireAuthorization(Permissions.DeployManage);

        g.MapPut("/groups/{id:guid}", async (Guid id, GroupBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (CheckGroup(body) is { } problem) return problem;
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.device_groups SET name = @n, description = @d, department_ids = @dep, location_ids = @loc, asset_ids = @as, platform = @pf,
                       app_mode = @m, wdac_mode = @w, updated_at = now() WHERE id = @id
                """);
            GroupParams(cmd, body);
            cmd.Parameters.AddWithValue("id", id);
            try { if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "Another group has this name."); }
            await ChangedAsync(ds, ct);
            await rc.AuditAsync("deployment.group_change", body.Name!.Trim(), detail: body);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeployManage);

        g.MapDelete("/groups/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.device_groups WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await ChangedAsync(ds, ct);
            await rc.AuditAsync("deployment.group_delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeployManage);

        g.MapGet("/groups/{id:guid}/devices", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($"""
                SELECT a.id, a.hostname, a.os_name, a.os_family, d.name, l.name, r.id IS NOT NULL, r.app_control_state, r.wdac_state
                  FROM zentriq.device_groups g, zentriq.assets a
                  LEFT JOIN zentriq.departments d ON d.id = a.department_id
                  LEFT JOIN zentriq.locations l ON l.id = a.location_id
                  LEFT JOIN LATERAL (SELECT x.id, x.app_control_state, x.wdac_state FROM zentriq.agent_registrations x WHERE x.asset_id = a.id AND x.status = 'active'
                                      ORDER BY x.last_checkin_at DESC NULLS LAST LIMIT 1) r ON true
                 WHERE g.id = @id AND a.status = 'active' AND {AppControlStore.MemberSql}
                 ORDER BY lower(a.hostname) LIMIT 5000
                """);
            cmd.Parameters.AddWithValue("id", id);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new { id = r.GetGuid(0), hostname = S(r, 1), osName = S(r, 2), osFamily = S(r, 3), department = S(r, 4), location = S(r, 5), agent = r.GetBoolean(6),
                    appControl = S(r, 7), wdac = S(r, 8) });
            return Results.Ok(list);
        });

        // ---- devices -------------------------------------------------------------------------------------------------
        g.MapGet("/devices", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var revision = (await AppControlStore.LoadSettingsAsync(ds, ct)).Revision;
            await using var cmd = ds.CreateCommand("""
                SELECT r.id, r.asset_id, COALESCE(a.hostname, r.hostname), r.platform, r.agent_version, r.app_control_state, r.wdac_state, r.remote_protection,
                       r.app_policy_revision, COALESCE('app-control' = ANY(r.capabilities), false), COALESCE('software-deploy' = ANY(r.capabilities), false),
                       r.online_until > now(), r.last_checkin_at, d.name, l.name
                  FROM zentriq.agent_registrations r LEFT JOIN zentriq.assets a ON a.id = r.asset_id
                  LEFT JOIN zentriq.departments d ON d.id = a.department_id LEFT JOIN zentriq.locations l ON l.id = a.location_id
                 WHERE r.status = 'active' ORDER BY lower(COALESCE(a.hostname, r.hostname)) LIMIT 10000
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    agentId = r.GetGuid(0), assetId = G(r, 1), hostname = S(r, 2), platform = r.GetString(3), agentVersion = S(r, 4), appControl = S(r, 5), wdac = S(r, 6),
                    remoteProtection = S(r, 7), policyCurrent = !r.IsDBNull(8) && r.GetInt64(8) == revision, appControlCapable = r.GetBoolean(9), deployCapable = r.GetBoolean(10),
                    online = !r.IsDBNull(11) && r.GetBoolean(11), lastCheckin = T(r, 12), department = S(r, 13), location = S(r, 14),
                });
            return Results.Ok(list);
        });

        // ---- events ---------------------------------------------------------------------------------------------------
        g.MapGet("/events", async (NpgsqlDataSource ds, string? kind, string? q, Guid? assetId, int? days, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT e.id, e.occurred_at, e.kind, e.name, e.publisher, e.version, e.sha256, e.path, e.user_name, e.platform, e.detail, e.asset_id, a.hostname
                  FROM zentriq.app_control_events e LEFT JOIN zentriq.assets a ON a.id = e.asset_id
                 WHERE e.occurred_at > now() - make_interval(days => @days) AND (@k::text IS NULL OR e.kind = @k) AND (@a::uuid IS NULL OR e.asset_id = @a)
                   AND (@q::text IS NULL OR e.name ILIKE @q OR e.publisher ILIKE @q OR e.user_name ILIKE @q OR a.hostname ILIKE @q OR e.path ILIKE @q)
                 ORDER BY e.occurred_at DESC LIMIT 2000
                """);
            cmd.Parameters.AddWithValue("days", Math.Clamp(days ?? 30, 1, 366));
            cmd.Parameters.Add(Text("k", string.IsNullOrWhiteSpace(kind) ? null : kind));
            cmd.Parameters.Add(Uuid("a", assetId));
            cmd.Parameters.Add(Text("q", string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim() + "%"));
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetInt64(0), at = r.GetFieldValue<DateTimeOffset>(1), kind = r.GetString(2), name = r.GetString(3), publisher = S(r, 4), version = S(r, 5),
                    sha256 = S(r, 6), path = S(r, 7), user = S(r, 8), platform = S(r, 9), detail = S(r, 10), assetId = G(r, 11), device = S(r, 12),
                });
            return Results.Ok(list);
        });

        // Approve what an event shows (typically from audit mode).
        g.MapPost("/events/{id:long}/approve", async (long id, DecideBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT name, publisher, version, sha256, platform, asset_id FROM zentriq.app_control_events WHERE id = @id");
            cmd.Parameters.AddWithValue("id", id);
            Seen? seen = null;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct)) seen = new Seen(r.GetString(0), S(r, 1), S(r, 2), S(r, 3), S(r, 4) ?? "windows", G(r, 5));
            if (seen is null) return Results.NotFound();
            var (rule, problem) = RuleFromSeen(seen, body);
            if (problem is not null) return problem;
            var ruleId = await InsertRuleAsync(ds, rule!, "manual", rc.UserId, ct);
            await ChangedAsync(ds, ct);
            await rc.AuditAsync("appcontrol.approve_event", rule!.Label, detail: rule);
            return Results.Ok(new { ruleId });
        }).RequireAuthorization(Permissions.DeployManage);

        // ---- requests -------------------------------------------------------------------------------------------------
        g.MapGet("/requests", async (NpgsqlDataSource ds, string? status, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT q.id, q.created_at, q.status, q.name, q.publisher, q.version, q.sha256, q.path, q.platform, q.user_name, q.reason,
                       q.asset_id, a.hostname, q.decided_at, u.email, q.decision_note, q.rule_id
                  FROM zentriq.app_control_requests q LEFT JOIN zentriq.assets a ON a.id = q.asset_id LEFT JOIN zentriq.users u ON u.id = q.decided_by
                 WHERE (@s::text IS NULL OR q.status = @s)
                 ORDER BY q.status = 'pending' DESC, q.created_at DESC LIMIT 2000
                """);
            cmd.Parameters.Add(Text("s", status is "pending" or "approved" or "rejected" ? status : null));
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), createdAt = r.GetFieldValue<DateTimeOffset>(1), status = r.GetString(2), name = r.GetString(3), publisher = S(r, 4), version = S(r, 5),
                    sha256 = S(r, 6), path = S(r, 7), platform = S(r, 8), user = S(r, 9), reason = S(r, 10), assetId = G(r, 11), device = S(r, 12),
                    decidedAt = T(r, 13), decidedBy = S(r, 14), decisionNote = S(r, 15), ruleId = G(r, 16),
                });
            return Results.Ok(list);
        });

        g.MapPost("/requests/{id:guid}/approve", async (Guid id, DecideBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("SELECT name, publisher, version, sha256, platform, asset_id FROM zentriq.app_control_requests WHERE id = @id AND status = 'pending'");
            cmd.Parameters.AddWithValue("id", id);
            Seen? seen = null;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct)) seen = new Seen(r.GetString(0), S(r, 1), S(r, 2), S(r, 3), S(r, 4) ?? "windows", G(r, 5));
            if (seen is null) return Results.NotFound();
            var (rule, problem) = RuleFromSeen(seen, body);
            if (problem is not null) return problem;
            var ruleId = await InsertRuleAsync(ds, rule!, "request", rc.UserId, ct);
            await using (var upd = ds.CreateCommand("""
                UPDATE zentriq.app_control_requests SET status = 'approved', decided_by = @by, decided_at = now(), decision_note = @no, rule_id = @r WHERE id = @id;
                -- Other open requests that the new rule answers are approved with it.
                UPDATE zentriq.app_control_requests SET status = 'approved', decided_by = @by, decided_at = now(), decision_note = 'Approved with another request.', rule_id = @r
                 WHERE status = 'pending' AND id <> @id AND lower(name) = lower(@n) AND (@all OR asset_id = @a);
                """))
            {
                upd.Parameters.Add(Uuid("by", rc.UserId));
                upd.Parameters.Add(Text("no", Trim(body.Note, 500)));
                upd.Parameters.AddWithValue("r", ruleId);
                upd.Parameters.AddWithValue("id", id);
                upd.Parameters.AddWithValue("n", seen.Name);
                upd.Parameters.AddWithValue("all", rule!.GroupId is null && rule.AssetId is null);
                upd.Parameters.Add(Uuid("a", seen.AssetId));
                await upd.ExecuteNonQueryAsync(ct);
            }
            await ChangedAsync(ds, ct);
            await rc.AuditAsync("appcontrol.request_approve", seen.Name, detail: rule);
            return Results.Ok(new { ruleId });
        }).RequireAuthorization(Permissions.DeployManage);

        g.MapPost("/requests/{id:guid}/reject", async (Guid id, DecideBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.app_control_requests SET status = 'rejected', decided_by = @by, decided_at = now(), decision_note = @no
                 WHERE id = @id AND status = 'pending' RETURNING name
                """);
            cmd.Parameters.Add(Uuid("by", rc.UserId));
            cmd.Parameters.Add(Text("no", Trim(body.Note, 500)));
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("appcontrol.request_reject", name, detail: new { body.Note });
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeployManage);

        // ---- packages -------------------------------------------------------------------------------------------------
        g.MapGet("/packages", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT p.id, p.name, p.version, p.publisher, p.platform, p.format, p.file_name, p.size, p.sha256, p.install_args, p.uninstall_args, p.catalog_key,
                       p.detect_name, p.notes, p.active, p.created_at, u.email,
                       (SELECT count(*) FROM zentriq.deployment_jobs j WHERE j.package_id = p.id),
                       (SELECT count(*) FROM zentriq.deployment_job_devices d JOIN zentriq.deployment_jobs j ON j.id = d.job_id JOIN zentriq.asset_actions x ON x.id = d.action_id
                         WHERE j.package_id = p.id AND j.operation = 'install' AND x.status = 'succeeded')
                  FROM zentriq.deployment_packages p LEFT JOIN zentriq.users u ON u.id = p.created_by
                 ORDER BY p.active DESC, lower(p.name), p.created_at DESC
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), name = r.GetString(1), version = S(r, 2), publisher = S(r, 3), platform = r.GetString(4), format = r.GetString(5), fileName = r.GetString(6),
                    size = r.GetInt64(7), sha256 = r.GetString(8), installArgs = S(r, 9), uninstallArgs = S(r, 10), catalogKey = S(r, 11), detectName = S(r, 12), notes = S(r, 13),
                    active = r.GetBoolean(14), createdAt = r.GetFieldValue<DateTimeOffset>(15), createdBy = S(r, 16), jobs = r.GetInt64(17), installed = r.GetInt64(18),
                });
            return Results.Ok(list);
        });

        // Step 1: the file (request body = the installer). Kept for a day until a package is created from it.
        g.MapPost("/packages/upload", async (HttpContext http, string? fileName, ZentriqPaths paths, CancellationToken ct) =>
        {
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = MaxPackage;
            var name = Path.GetFileName(fileName ?? "").Trim();
            var format = FormatOf(name);
            if (format is null) return Validation.Problem("file", "Upload a Windows Installer (.msi), a setup program (.exe), a Debian package (.deb) or an RPM package (.rpm).");
            var dir = UploadDir(paths);
            Directory.CreateDirectory(dir);
            foreach (var old in new DirectoryInfo(dir).EnumerateFiles().Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
                try { old.Delete(); } catch (IOException) { }
            var id = Guid.NewGuid().ToString("N");
            var path = Path.Combine(dir, id + ".upload");
            string sha; long length;
            try
            {
                await using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buf = new byte[1 << 20];
                    int n;
                    while ((n = await http.Request.Body.ReadAsync(buf, ct)) > 0) { hash.AppendData(buf, 0, n); await f.WriteAsync(buf.AsMemory(0, n), ct); }
                    sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    length = f.Length;
                }
                if (length == 0) { File.Delete(path); return Validation.Problem("file", "The file is empty."); }
                await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(new { fileName = name, sha256 = sha, size = length, format }), ct);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Problem(statusCode: 403, title: "The console cannot write the Deployment folder yet. Run Zentriq Setup (Repair) once.");
            }
            var guess = GuessFromFileName(name);
            return Results.Ok(new { uploadId = id, fileName = name, sha256 = sha, size = length, format, platform = format is "deb" or "rpm" ? "linux" : "windows",
                suggestedName = guess.Name, suggestedVersion = guess.Version, installArgs = DefaultInstallArgs(format), uninstallArgs = format == "msi" ? "/qn /norestart" : null });
        }).RequireAuthorization(Permissions.DeployManage).DisableAntiforgery();

        // Step 2: the package (what it is and how it installs).
        g.MapPost("/packages", async (PackageBody body, NpgsqlDataSource ds, ZentriqPaths paths, RequestContext rc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.UploadId) || body.UploadId.Length != 32 || !body.UploadId.All(char.IsAsciiHexDigit)) return Validation.Problem("uploadId", "Upload the installer first.");
            var upload = Path.Combine(UploadDir(paths), body.UploadId + ".upload");
            if (!File.Exists(upload) || !File.Exists(upload + ".json")) return Validation.Problem("uploadId", "The uploaded file is no longer there; upload it again.");
            var meta = JsonDocument.Parse(await File.ReadAllTextAsync(upload + ".json", ct)).RootElement;
            var format = meta.GetProperty("format").GetString()!;
            var name = Trim(body.Name, 200);
            if (name is null) return Validation.Problem("name", "Enter the software name.");
            var platform = format is "deb" or "rpm" ? "linux" : "windows";
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.deployment_packages (id, name, version, publisher, platform, format, file_name, size, sha256, install_args, uninstall_args, catalog_key, detect_name, notes, created_by)
                VALUES (@id, @n, @v, @p, @pf, @f, @fn, @s, @h, @ia, @ua, @ck, @dn, @no, @by)
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.Add(Text("v", Trim(body.Version, 60)));
            cmd.Parameters.Add(Text("p", Trim(body.Publisher, 200)));
            cmd.Parameters.AddWithValue("pf", platform);
            cmd.Parameters.AddWithValue("f", format);
            cmd.Parameters.AddWithValue("fn", meta.GetProperty("fileName").GetString()!);
            cmd.Parameters.AddWithValue("s", meta.GetProperty("size").GetInt64());
            cmd.Parameters.AddWithValue("h", meta.GetProperty("sha256").GetString()!);
            cmd.Parameters.Add(Text("ia", Trim(body.InstallArgs, 1000) ?? DefaultInstallArgs(format)));
            cmd.Parameters.Add(Text("ua", Trim(body.UninstallArgs, 1000)));
            cmd.Parameters.Add(Text("ck", Trim(body.CatalogKey, 100)));
            cmd.Parameters.Add(Text("dn", Trim(body.DetectName, 200)));
            cmd.Parameters.Add(Text("no", Trim(body.Notes, 1000)));
            cmd.Parameters.Add(Uuid("by", rc.UserId));
            Directory.CreateDirectory(paths.DeploymentPackages);
            File.Move(upload, DeploymentFiles.PathOf(paths, id));
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch { File.Move(DeploymentFiles.PathOf(paths, id), upload); throw; }
            File.Delete(upload + ".json");
            // The software is approved (so it is not reported or removed once installed), unless the administrator chose otherwise.
            if (body.Approve != false)
            {
                await InsertRuleAsync(ds, new Rule("allow", name, Escape(Trim(body.DetectName, 200) ?? name) + (body.DetectName is null ? "*" : ""), null, null, null, null, platform, null, null,
                    "Deployment package " + name + (body.Version is null ? "" : " " + body.Version), null), "package", rc.UserId, ct);
                await InsertRuleAsync(ds, new Rule("allow", name + " (installer)", null, null, null, null, meta.GetProperty("sha256").GetString(), platform, null, null,
                    "Installer of deployment package " + name, null), "package", rc.UserId, ct);
                await ChangedAsync(ds, ct);
            }
            await rc.AuditAsync("deployment.package_add", name, detail: new { body.Version, format, size = meta.GetProperty("size").GetInt64() });
            return Results.Created($"/api/v1/deployment/packages/{id}", new { id });
        }).RequireAuthorization(Permissions.DeployManage);

        g.MapPut("/packages/{id:guid}", async (Guid id, PackageBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var name = Trim(body.Name, 200);
            if (name is null) return Validation.Problem("name", "Enter the software name.");
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.deployment_packages SET name = @n, version = @v, publisher = @p, install_args = @ia, uninstall_args = @ua, catalog_key = @ck,
                       detect_name = @dn, notes = @no, active = COALESCE(@act, active)
                 WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.Add(Text("v", Trim(body.Version, 60)));
            cmd.Parameters.Add(Text("p", Trim(body.Publisher, 200)));
            cmd.Parameters.Add(Text("ia", Trim(body.InstallArgs, 1000)));
            cmd.Parameters.Add(Text("ua", Trim(body.UninstallArgs, 1000)));
            cmd.Parameters.Add(Text("ck", Trim(body.CatalogKey, 100)));
            cmd.Parameters.Add(Text("dn", Trim(body.DetectName, 200)));
            cmd.Parameters.Add(Text("no", Trim(body.Notes, 1000)));
            cmd.Parameters.Add(new NpgsqlParameter("act", NpgsqlDbType.Boolean) { Value = (object?)body.Active ?? DBNull.Value });
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await rc.AuditAsync("deployment.package_change", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeployManage);

        g.MapDelete("/packages/{id:guid}", async (Guid id, NpgsqlDataSource ds, ZentriqPaths paths, RequestContext rc, CancellationToken ct) =>
        {
            await using (var open = ds.CreateCommand("SELECT 1 FROM zentriq.asset_actions WHERE kind = 'deploy' AND target = @t AND status IN ('queued','sent','running') LIMIT 1"))
            {
                open.Parameters.AddWithValue("t", id.ToString());
                if (await open.ExecuteScalarAsync(ct) is not null) return Results.Problem(statusCode: 409, title: "Deployments of this package are still running. Cancel them first.");
            }
            await using (var used = ds.CreateCommand("SELECT 1 FROM zentriq.deployment_jobs WHERE package_id = @id LIMIT 1"))
            {
                used.Parameters.AddWithValue("id", id);
                if (await used.ExecuteScalarAsync(ct) is not null)
                {
                    // Deployed before: kept for the history, without its file.
                    await using var off = ds.CreateCommand("UPDATE zentriq.deployment_packages SET active = false WHERE id = @id RETURNING name");
                    off.Parameters.AddWithValue("id", id);
                    var n = await off.ExecuteScalarAsync(ct) as string;
                    try { File.Delete(DeploymentFiles.PathOf(paths, id)); } catch (IOException) { }
                    await rc.AuditAsync("deployment.package_retire", n);
                    return Results.NoContent();
                }
            }
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.deployment_packages WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            try { File.Delete(DeploymentFiles.PathOf(paths, id)); } catch (IOException) { }
            await rc.AuditAsync("deployment.package_delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.DeployManage);

        // ---- deployments ------------------------------------------------------------------------------------------------
        g.MapGet("/jobs", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT j.id, j.created_at, j.operation, j.note, p.id, p.name, p.version, p.platform, u.email, j.targets,
                       count(d.asset_id), count(*) FILTER (WHERE d.skipped IS NOT NULL),
                       count(*) FILTER (WHERE x.status IN ('queued','sent')), count(*) FILTER (WHERE x.status = 'running'),
                       count(*) FILTER (WHERE x.status = 'succeeded'), count(*) FILTER (WHERE x.status IN ('failed','expired'))
                  FROM zentriq.deployment_jobs j JOIN zentriq.deployment_packages p ON p.id = j.package_id
                  LEFT JOIN zentriq.users u ON u.id = j.created_by
                  LEFT JOIN zentriq.deployment_job_devices d ON d.job_id = j.id
                  LEFT JOIN zentriq.asset_actions x ON x.id = d.action_id
                 GROUP BY j.id, p.id, u.email ORDER BY j.created_at DESC LIMIT 500
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new
                {
                    id = r.GetGuid(0), createdAt = r.GetFieldValue<DateTimeOffset>(1), operation = r.GetString(2), note = S(r, 3), packageId = r.GetGuid(4), package = r.GetString(5),
                    version = S(r, 6), platform = r.GetString(7), createdBy = S(r, 8), targets = JsonDocument.Parse(r.GetString(9)).RootElement,
                    devices = r.GetInt64(10), skipped = r.GetInt64(11), waiting = r.GetInt64(12), running = r.GetInt64(13), succeeded = r.GetInt64(14), failed = r.GetInt64(15),
                });
            return Results.Ok(list);
        });

        g.MapGet("/jobs/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT d.asset_id, a.hostname, d.skipped, x.id, x.status, x.detail, x.result_state, x.requested_at, x.sent_at, x.completed_at
                  FROM zentriq.deployment_job_devices d JOIN zentriq.assets a ON a.id = d.asset_id LEFT JOIN zentriq.asset_actions x ON x.id = d.action_id
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
                    assetId = r.GetGuid(0), hostname = S(r, 1), status = skipped is not null ? "skipped" : S(r, 4) ?? "unknown", detail = skipped ?? S(r, 5), result = S(r, 6),
                    actionId = G(r, 3), requestedAt = T(r, 7), sentAt = T(r, 8), completedAt = T(r, 9),
                });
            }
            return list.Count == 0 && !await ExistsAsync(ds, "SELECT 1 FROM zentriq.deployment_jobs WHERE id = @id", id, ct) ? Results.NotFound() : Results.Ok(list);
        });

        g.MapPost("/jobs", async (JobBody body, NpgsqlDataSource ds, ZentriqPaths paths, RequestContext rc, CancellationToken ct) =>
        {
            var op = body.Operation == "uninstall" ? "uninstall" : "install";
            await using var pc = ds.CreateCommand("SELECT name, version, publisher, platform, format, file_name, size, sha256, install_args, uninstall_args, detect_name, active FROM zentriq.deployment_packages WHERE id = @id");
            pc.Parameters.AddWithValue("id", body.PackageId);
            Dictionary<string, object?>? argument = null;
            string? pkgName = null, platform = null, detect = null;
            await using (var r = await pc.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct))
                {
                    if (!r.GetBoolean(11)) return Validation.Problem("packageId", "This package was retired.");
                    pkgName = r.GetString(0); platform = r.GetString(3); detect = S(r, 10);
                    argument = new()
                    {
                        ["name"] = pkgName, ["version"] = S(r, 1), ["publisher"] = S(r, 2), ["format"] = r.GetString(4), ["fileName"] = r.GetString(5), ["size"] = r.GetInt64(6),
                        ["sha256"] = r.GetString(7), ["installArgs"] = S(r, 8), ["uninstallArgs"] = S(r, 9), ["detectName"] = detect,
                    };
                }
            if (argument is null) return Validation.Problem("packageId", "Choose a package.");
            if (op == "install" && !File.Exists(DeploymentFiles.PathOf(paths, body.PackageId))) return Validation.Problem("packageId", "The package file is missing on the server. Upload the package again.");
            var targets = await ResolveTargetsAsync(ds, body, ct);
            if (targets.Count == 0) return Validation.Problem("assetIds", "Choose the devices (or groups, departments, all devices).");
            if (targets.Count > 10000) return Validation.Problem("assetIds", "Deploy to at most 10 000 devices at a time.");

            var jobId = Guid.NewGuid();
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var j = new NpgsqlCommand("INSERT INTO zentriq.deployment_jobs (id, package_id, operation, targets, note, created_by) VALUES (@id, @p, @op, @t::jsonb, @n, @by)", conn, tx))
            {
                j.Parameters.AddWithValue("id", jobId);
                j.Parameters.AddWithValue("p", body.PackageId);
                j.Parameters.AddWithValue("op", op);
                j.Parameters.AddWithValue("t", JsonSerializer.Serialize(new { body.AssetIds, body.GroupIds, body.DepartmentIds, all = body.All == true }, Json));
                j.Parameters.Add(Text("n", Trim(body.Note, 500)));
                j.Parameters.Add(Uuid("by", rc.UserId));
                await j.ExecuteNonQueryAsync(ct);
            }
            var argJson = JsonSerializer.Serialize(argument, Json);
            int sent = 0, skipped = 0;
            foreach (var t in targets)
            {
                string? why = t.AgentId is null ? "No Zentriq Agent on this device."
                    : AppControlStore.Family(t.Platform) != platform ? $"The package is for {(platform == "linux" ? "Linux" : "Windows")}; this device runs {(AppControlStore.Family(t.Platform) == "linux" ? "Linux" : "Windows")}."
                    : !t.CanDeploy ? "The agent on this device is too old for deployments; update the agent first." : null;
                Guid? actionId = null;
                if (why is null)
                {
                    await using var a = new NpgsqlCommand("""
                        INSERT INTO zentriq.asset_actions (asset_id, agent_id, channel, kind, target, operation, argument, requested_by, detail)
                        VALUES (@as, @ag, 'agent', 'deploy', @t, @op, @arg, @by, @d) RETURNING id
                        """, conn, tx);
                    a.Parameters.AddWithValue("as", t.AssetId);
                    a.Parameters.AddWithValue("ag", t.AgentId!.Value);
                    a.Parameters.AddWithValue("t", body.PackageId.ToString());
                    a.Parameters.AddWithValue("op", op);
                    a.Parameters.AddWithValue("arg", argJson);
                    a.Parameters.Add(Uuid("by", rc.UserId));
                    a.Parameters.AddWithValue("d", $"{(op == "install" ? "Install" : "Uninstall")} {pkgName}{(argument["version"] is string v ? " " + v : "")}");
                    actionId = (Guid)(await a.ExecuteScalarAsync(ct))!;
                    sent++;
                }
                else skipped++;
                await using var d = new NpgsqlCommand("INSERT INTO zentriq.deployment_job_devices (job_id, asset_id, action_id, skipped) VALUES (@j, @a, @x, @s)", conn, tx);
                d.Parameters.AddWithValue("j", jobId);
                d.Parameters.AddWithValue("a", t.AssetId);
                d.Parameters.Add(Uuid("x", actionId));
                d.Parameters.Add(Text("s", why));
                await d.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            AgentPresence.Instance.Nudge();
            await rc.AuditAsync(op == "install" ? "deployment.install" : "deployment.uninstall", pkgName, detail: new { jobId, sent, skipped });
            return Results.Created($"/api/v1/deployment/jobs/{jobId}", new { id = jobId, sent, skipped });
        }).RequireAuthorization(Permissions.DeployRun);

        // Send the package again to the devices where it failed (or expired).
        g.MapPost("/jobs/{id:guid}/retry", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                WITH failed AS (
                    SELECT d.job_id, d.asset_id, x.agent_id, x.target, x.operation, x.argument, x.detail AS old
                      FROM zentriq.deployment_job_devices d JOIN zentriq.asset_actions x ON x.id = d.action_id
                     WHERE d.job_id = @id AND x.status IN ('failed','expired')
                       AND EXISTS (SELECT 1 FROM zentriq.agent_registrations r WHERE r.id = x.agent_id AND r.status = 'active')),
                ins AS (
                    INSERT INTO zentriq.asset_actions (asset_id, agent_id, channel, kind, target, operation, argument, requested_by, detail)
                    SELECT f.asset_id, f.agent_id, 'agent', 'deploy', f.target, f.operation, f.argument, @by,
                           (SELECT CASE j.operation WHEN 'install' THEN 'Install ' ELSE 'Uninstall ' END || p.name || COALESCE(' ' || p.version, '') || ' (retry)'
                              FROM zentriq.deployment_jobs j JOIN zentriq.deployment_packages p ON p.id = j.package_id WHERE j.id = f.job_id)
                      FROM failed f RETURNING id, asset_id)
                UPDATE zentriq.deployment_job_devices d SET action_id = ins.id FROM ins WHERE d.job_id = @id AND d.asset_id = ins.asset_id
                RETURNING d.asset_id
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.Add(Uuid("by", rc.UserId));
            var n = 0;
            await using (var r = await cmd.ExecuteReaderAsync(ct)) while (await r.ReadAsync(ct)) n++;
            if (n > 0) AgentPresence.Instance.Nudge();
            await rc.AuditAsync("deployment.retry", id.ToString(), detail: new { devices = n });
            return Results.Ok(new { retried = n });
        }).RequireAuthorization(Permissions.DeployRun);

        g.MapPost("/jobs/{id:guid}/cancel", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.asset_actions x SET status = 'expired', completed_at = now(), detail = 'Cancelled by ' || @u || ' before the device started it.'
                  FROM zentriq.deployment_job_devices d WHERE d.job_id = @id AND x.id = d.action_id AND x.status = 'queued'
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("u", rc.UserName);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("deployment.cancel", id.ToString(), detail: new { devices = n });
            return Results.Ok(new { cancelled = n });
        }).RequireAuthorization(Permissions.DeployRun);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static object SettingsJson(AppControlStore.Settings s) => new
    {
        s.Mode, s.Message, s.AllowRequests, s.AutoRemove, s.ProtectRemoteAgent, s.Revision, s.EnabledAt, defaultMessage = AppControl.DefaultMessage,
    };

    private sealed record Rule(string Action, string Label, string? Name, string? Publisher, string? VersionMin, string? VersionMax, string? Sha256,
        string? Platform, Guid? GroupId, Guid? AssetId, string? Note, DateTimeOffset? ExpiresAt);
    private sealed record Seen(string Name, string? Publisher, string? Version, string? Sha256, string Platform, Guid? AssetId);
    private sealed record Target(Guid AssetId, Guid? AgentId, string? Platform, bool CanDeploy);

    private static (Rule?, IResult?) CheckRule(RuleBody b)
    {
        var action = b.Action == "block" ? "block" : "allow";
        var name = Trim(b.Name, 300); var publisher = Trim(b.Publisher, 300);
        string? sha = null;
        if (!string.IsNullOrWhiteSpace(b.Sha256) && (sha = AppControl.NormalizeSha256(b.Sha256)) is null) return (null, Validation.Problem("sha256", "Enter the SHA-256 as 64 hexadecimal characters."));
        if (name is null && publisher is null && sha is null) return (null, Validation.Problem("name", "Enter the software name, the publisher or the installer's SHA-256."));
        if (b.Platform is not (null or "" or "windows" or "linux")) return (null, Validation.Problem("platform", "Choose Windows, Linux or both."));
        if (b.GroupId is not null && b.AssetId is not null) return (null, Validation.Problem("groupId", "Choose a device group or one device, not both."));
        var label = Trim(b.Label, 200) ?? name ?? publisher ?? "SHA-256 " + sha![..12];
        return (new Rule(action, label, name, publisher, Trim(b.VersionMin, 60), Trim(b.VersionMax, 60), sha, string.IsNullOrEmpty(b.Platform) ? null : b.Platform,
            b.GroupId, b.AssetId, Trim(b.Note, 1000), b.ExpiresAt), null);
    }

    private static (Rule?, IResult?) RuleFromSeen(Seen s, DecideBody b)
    {
        var match = b.Match ?? (s.Publisher is not null && s.Platform == "windows" ? "name-publisher" : "name");
        var scope = b.Scope ?? "all";
        Guid? group = scope == "group" ? b.GroupId : null, asset = scope == "device" ? s.AssetId : null;
        if (scope == "group" && group is null) return (null, Validation.Problem("groupId", "Choose the device group."));
        if (scope == "device" && asset is null) return (null, Validation.Problem("scope", "The device is not known; approve it for all devices or a group."));
        return match switch
        {
            "hash" when s.Sha256 is null => (null, Validation.Problem("match", "No SHA-256 is known for this file; approve it by name.")),
            "hash" => (new Rule("allow", s.Name, null, null, null, null, s.Sha256, s.Platform, group, asset, Trim(b.Note, 1000), null), null),
            "publisher" when s.Publisher is null => (null, Validation.Problem("match", "No publisher is known for this software; approve it by name.")),
            "publisher" => (new Rule("allow", s.Publisher!, null, Escape(s.Publisher!), null, null, null, s.Platform, group, asset, Trim(b.Note, 1000), null), null),
            "name-version" => (new Rule("allow", s.Name + (s.Version is null ? "" : " " + s.Version), Escape(s.Name), null, s.Version, s.Version, null, s.Platform, group, asset, Trim(b.Note, 1000), null), null),
            "name-publisher" when s.Publisher is not null => (new Rule("allow", s.Name, Escape(s.Name), Escape(s.Publisher), null, null, null, s.Platform, group, asset, Trim(b.Note, 1000), null), null),
            _ => (new Rule("allow", s.Name, Escape(s.Name), null, null, null, null, s.Platform, group, asset, Trim(b.Note, 1000), null), null),
        };
    }

    private static async Task<Guid> InsertRuleAsync(NpgsqlDataSource ds, Rule rule, string source, Guid? by, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.app_control_rules (action, label, name_pattern, publisher_pattern, version_min, version_max, sha256, platform, group_id, asset_id, note, expires_at, source, created_by)
            VALUES (@act, @l, @n, @p, @vmin, @vmax, @h, @pf, @g, @a, @no, @x, @src, @by) RETURNING id
            """);
        RuleParams(cmd, rule);
        cmd.Parameters.AddWithValue("src", source);
        cmd.Parameters.Add(Uuid("by", by));
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static void RuleParams(NpgsqlCommand cmd, Rule r)
    {
        cmd.Parameters.AddWithValue("act", r.Action);
        cmd.Parameters.AddWithValue("l", r.Label);
        cmd.Parameters.Add(Text("n", r.Name));
        cmd.Parameters.Add(Text("p", r.Publisher));
        cmd.Parameters.Add(Text("vmin", r.VersionMin));
        cmd.Parameters.Add(Text("vmax", r.VersionMax));
        cmd.Parameters.Add(Text("h", r.Sha256));
        cmd.Parameters.Add(Text("pf", r.Platform));
        cmd.Parameters.Add(Uuid("g", r.GroupId));
        cmd.Parameters.Add(Uuid("a", r.AssetId));
        cmd.Parameters.Add(Text("no", r.Note));
        cmd.Parameters.Add(new NpgsqlParameter("x", NpgsqlDbType.TimestampTz) { Value = (object?)r.ExpiresAt ?? DBNull.Value });
    }

    private static IResult? CheckGroup(GroupBody b)
    {
        var name = b.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return Validation.Problem("name", "Enter the group name (up to 100 characters).");
        if (b.Platform is not (null or "" or "windows" or "linux")) return Validation.Problem("platform", "Choose Windows, Linux or both.");
        if (b.AppMode is not (null or "" or "off" or "audit" or "block")) return Validation.Problem("appMode", "Choose the organisation's mode, off, audit or block.");
        if (b.WdacMode is not (null or "" or "off" or "audit" or "enforce")) return Validation.Problem("wdacMode", "Choose off, audit or enforce.");
        if ((b.DepartmentIds?.Length ?? 0) + (b.LocationIds?.Length ?? 0) + (b.AssetIds?.Length ?? 0) == 0)
            return Validation.Problem("departmentIds", "Choose the departments, locations or devices of the group.");
        return null;
    }

    private static void GroupParams(NpgsqlCommand cmd, GroupBody b)
    {
        cmd.Parameters.AddWithValue("n", b.Name!.Trim());
        cmd.Parameters.Add(Text("d", Trim(b.Description, 500)));
        cmd.Parameters.AddWithValue("dep", (b.DepartmentIds ?? []).Distinct().ToArray());
        cmd.Parameters.AddWithValue("loc", (b.LocationIds ?? []).Distinct().ToArray());
        cmd.Parameters.AddWithValue("as", (b.AssetIds ?? []).Distinct().Take(10000).ToArray());
        cmd.Parameters.Add(Text("pf", string.IsNullOrEmpty(b.Platform) ? null : b.Platform));
        cmd.Parameters.Add(Text("m", string.IsNullOrEmpty(b.AppMode) ? null : b.AppMode));
        cmd.Parameters.AddWithValue("w", string.IsNullOrEmpty(b.WdacMode) ? "off" : b.WdacMode);
    }

    private static async Task<List<Target>> ResolveTargetsAsync(NpgsqlDataSource ds, JobBody b, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"""
            SELECT a.id, r.id, r.platform, COALESCE('software-deploy' = ANY(r.capabilities), false)
              FROM zentriq.assets a
              LEFT JOIN LATERAL (SELECT x.id, x.platform, x.capabilities FROM zentriq.agent_registrations x WHERE x.asset_id = a.id AND x.status = 'active'
                                  ORDER BY x.last_checkin_at DESC NULLS LAST LIMIT 1) r ON true
             WHERE a.status = 'active' AND (
                   @all OR a.id = ANY(@as) OR a.department_id = ANY(@dep)
                   OR EXISTS (SELECT 1 FROM zentriq.device_groups g WHERE g.id = ANY(@grp) AND {AppControlStore.MemberSql}))
               AND (NOT @all OR r.id IS NOT NULL)
             LIMIT 10001
            """);
        cmd.Parameters.AddWithValue("all", b.All == true);
        cmd.Parameters.AddWithValue("as", b.AssetIds ?? []);
        cmd.Parameters.AddWithValue("dep", b.DepartmentIds ?? []);
        cmd.Parameters.AddWithValue("grp", b.GroupIds ?? []);
        var list = new List<Target>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new Target(r.GetGuid(0), G(r, 1), S(r, 2), r.GetBoolean(3)));
        return list;
    }

    private static async Task<List<SoftwareCatalog.Entry>> AllCatalogAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        var list = SoftwareCatalog.Standard.Entries.ToList();
        await using var cmd = ds.CreateCommand("SELECT id, key, name, publisher, category, platforms, name_patterns, publisher_patterns, website, notes FROM zentriq.software_catalog");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SoftwareCatalog.Entry
            {
                Id = r.GetGuid(0), Key = r.GetString(1), Name = r.GetString(2), Publisher = S(r, 3), Category = r.GetString(4), Platforms = [.. r.GetFieldValue<string[]>(5)],
                NamePatterns = [.. r.GetFieldValue<string[]>(6)], PublisherPatterns = [.. r.GetFieldValue<string[]>(7)], Website = S(r, 8), Notes = S(r, 9), BuiltIn = false,
            });
        return list;
    }

    /// <summary>On how many devices each catalogue entry is installed (by its rules, any version).</summary>
    private static async Task<Dictionary<string, long>> CatalogInstallsAsync(NpgsqlDataSource ds, List<SoftwareCatalog.Entry> entries, CancellationToken ct)
    {
        var rows = new List<(string Name, string? Publisher, string Platform, long Devices)>();
        await using (var cmd = ds.CreateCommand("""
            SELECT s.name, s.publisher, CASE WHEN a.os_family = 'linux' THEN 'linux' ELSE 'windows' END, count(DISTINCT s.asset_id)
              FROM zentriq.software_inventory s JOIN zentriq.assets a ON a.id = s.asset_id
             WHERE a.status = 'active' AND a.os_family IN ('windows','linux') GROUP BY 1, 2, 3 LIMIT 50000
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), S(r, 1), r.GetString(2), r.GetInt64(3)));
        var result = new Dictionary<string, long>();
        foreach (var e in entries)
        {
            var rules = SoftwareCatalog.RulesFor(e).Select(x => new AppControlRule { Name = x.Name, Publisher = x.Publisher, Platform = x.Platform }).ToList();
            long n = 0;
            foreach (var row in rows)
                if (rules.Any(rule => AppControl.Matches(rule, new SoftwareCandidate { Name = row.Name, Publisher = row.Publisher, Platform = row.Platform }))) n += row.Devices;
            if (n > 0) result[e.Key] = n;
        }
        return result;
    }

    private static async Task ChangedAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await AppControlStore.BumpRevisionAsync(ds, ct);
        AgentPresence.Instance.Nudge();
    }

    private static async Task<HashSet<string>> StringsAsync(NpgsqlDataSource ds, string sql, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(sql);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    private static async Task<bool> ExistsAsync(NpgsqlDataSource ds, string sql, Guid id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static string UploadDir(ZentriqPaths paths) => Path.Combine(paths.DeploymentPackages, "Uploads");

    internal static string? FormatOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".msi" => "msi", ".exe" => "exe", ".deb" => "deb", ".rpm" => "rpm", _ => null,
    };

    internal static string? DefaultInstallArgs(string format) => format switch { "msi" => "/qn /norestart", "exe" => "/S", _ => null };

    /// <summary>"Firefox Setup 128.0.2.exe" → (Firefox, 128.0.2); "code_1.95.3-1731513102_amd64.deb" → (code, 1.95.3-1731513102).</summary>
    internal static (string? Name, string? Version) GuessFromFileName(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName);
        var deb = System.Text.RegularExpressions.Regex.Match(n, "^([a-z0-9][a-z0-9+.-]+)_([^_]+)_[a-z0-9]+$");
        if (deb.Success) return (deb.Groups[1].Value, deb.Groups[2].Value);
        var v = System.Text.RegularExpressions.Regex.Match(n, "(\\d+(?:[._]\\d+)+)");
        return (AppControl.InstallerBaseName(fileName), v.Success ? v.Groups[1].Value.Replace('_', '.') : null);
    }

    private static string Escape(string s) => s.Replace("*", "?").Replace(";", "?");
    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
    private static string[] Clean(string[]? v) => (v ?? []).Select(x => x?.Trim() ?? "").Where(x => x.Length is > 0 and <= 200).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
    private static string Slug(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    private static NpgsqlParameter Text(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = (object?)v ?? DBNull.Value };
    private static NpgsqlParameter Uuid(string n, Guid? v) => new(n, NpgsqlDbType.Uuid) { Value = (object?)v ?? DBNull.Value };
    private static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static Guid? G(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);
    private static DateTimeOffset? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);
}
