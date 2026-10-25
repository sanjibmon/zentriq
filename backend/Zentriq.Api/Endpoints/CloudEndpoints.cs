using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Cloud;
using Zentriq.Infrastructure.Jobs;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Cloud &amp; Infrastructure: read-only connections to Microsoft Azure, Amazon Web Services, VMware vCenter and Hyper-V
/// hosts; the resources they report (virtual machines, hosts, datastores, disks, IPs, networks, security groups, load
/// balancers, storage, databases, Kubernetes, functions), the inventoried device each machine is, changes and findings.
/// Secrets are write-only (encrypted with the credential key).
/// </summary>
public static class CloudEndpoints
{
    public sealed record AccountBody(string Name, string Provider, string? TenantId, string? ClientId, string[]? Subscriptions, string? AccessKeyId, string[]? Regions,
        string? Host, int? Port, string? Username, bool? SkipCertificateCheck, string[]? Hosts, string[]? Projects, string? Secret, Guid? CredentialId, bool? Enabled, int? SyncHours);
    public sealed record LinkBody(Guid? AssetId, bool? Automatic);
    public sealed record IgnoreBody(bool Ignored);

    private static async Task<JsonElement> JsonAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var json = await cmd.ExecuteScalarAsync(ct) as string ?? "null";
        return JsonDocument.Parse(json).RootElement.Clone();
    }
    private static NpgsqlParameter T(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(v) ? DBNull.Value : v.Trim() };
    private static NpgsqlParameter U(string n, Guid? v) => new(n, NpgsqlDbType.Uuid) { Value = (object?)v ?? DBNull.Value };

    /// <summary>A resource as JSON (alias r, account c, asset a).</summary>
    private const string ResourceJson = """
        json_build_object('id', r.id, 'accountId', r.account_id, 'account', c.name, 'provider', r.provider, 'kind', r.kind, 'externalId', r.external_id,
          'name', r.name, 'region', r.region, 'group', r.resource_group, 'parentId', r.parent_id, 'state', r.state, 'stateSince', r.state_since,
          'size', r.size, 'os', r.os, 'hostname', r.hostname, 'privateIp', r.private_ip, 'publicIp', r.public_ip, 'cpu', r.cpu, 'memoryMb', r.memory_mb,
          'diskGb', r.disk_gb, 'biosUuid', r.bios_uuid, 'macs', r.macs, 'tags', r.tags, 'properties', r.properties, 'createdAt', r.created_at_provider,
          'assetId', r.asset_id, 'assetName', COALESCE(a.hostname, a.serial_number), 'assetLinkManual', r.asset_link_manual,
          'department', (SELECT d.name FROM zentriq.departments d WHERE d.id = r.department_id), 'location', (SELECT l.name FROM zentriq.locations l WHERE l.id = r.location_id),
          'firstSeen', r.first_seen_at, 'lastSeen', r.last_seen_at, 'deletedAt', r.deleted_at,
          'openFindings', (SELECT count(*) FROM zentriq.cloud_findings f WHERE f.resource_id = r.id AND f.resolved_at IS NULL AND NOT f.ignored),
          'worstSeverity', (SELECT f.severity FROM zentriq.cloud_findings f WHERE f.resource_id = r.id AND f.resolved_at IS NULL AND NOT f.ignored
                             ORDER BY array_position(ARRAY['critical','high','medium','low','info'], f.severity) LIMIT 1))
        """;
    private const string ResourceFrom = """
        zentriq.cloud_resources r JOIN zentriq.cloud_accounts c ON c.id = r.account_id LEFT JOIN zentriq.assets a ON a.id = r.asset_id
        """;
    private const string RunningStates = "('running','poweredon','powered_on','active','available','succeeded','ready','connected','powered on')";

    public static void MapCloudEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/cloud").WithTags("Cloud & infrastructure").RequireModule(Modules.Cloud).RequireAuthorization(Permissions.CloudView);

        g.MapGet("/meta", () => Results.Ok(new
        {
            providers = CloudProviders.All.Select(p => new { id = p, label = CloudProviders.Title(p) }),
            kinds = CloudKinds.Labels.Select(k => new { id = k.Key, label = k.Value }),
            rules = CloudRules.RuleTitles.Select(r => new { id = r.Key, label = r.Value }),
            departmentTagKeys = CloudStore.DepartmentTagKeys, locationTagKeys = CloudStore.LocationTagKeys,
        }));

        g.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($$"""
                WITH live AS (SELECT * FROM zentriq.cloud_resources WHERE deleted_at IS NULL)
                SELECT json_build_object(
                  'accounts', (SELECT count(*) FROM zentriq.cloud_accounts),
                  'accountsFailed', (SELECT count(*) FROM zentriq.cloud_accounts WHERE enabled AND last_status = 'failed'),
                  'resources', (SELECT count(*) FROM live),
                  'vms', (SELECT count(*) FROM live WHERE kind = 'vm'),
                  'vmsRunning', (SELECT count(*) FROM live WHERE kind = 'vm' AND lower(state) IN {{RunningStates}}),
                  'vmsLinked', (SELECT count(*) FROM live WHERE kind = 'vm' AND asset_id IS NOT NULL),
                  'hosts', (SELECT count(*) FROM live WHERE kind = 'host'),
                  'vcpu', (SELECT COALESCE(sum(cpu), 0) FROM live WHERE kind = 'vm'),
                  'memoryGb', (SELECT COALESCE(round(sum(memory_mb) / 1024.0), 0) FROM live WHERE kind = 'vm'),
                  'storageGb', (SELECT COALESCE(round(sum(disk_gb)), 0) FROM live WHERE kind IN ('disk','datastore') OR (kind = 'vm' AND provider IN ('vmware','hyperv'))),
                  'byProvider', (SELECT COALESCE(json_agg(x ORDER BY x.n DESC), '[]') FROM (
                       SELECT provider, count(*) n, count(*) FILTER (WHERE kind = 'vm') vms,
                              count(*) FILTER (WHERE kind = 'vm' AND lower(state) IN {{RunningStates}}) running FROM live GROUP BY provider) x),
                  'byKind', (SELECT COALESCE(json_object_agg(kind, n), '{}') FROM (SELECT kind, count(*) n FROM live GROUP BY kind) x),
                  'findings', (SELECT COALESCE(json_object_agg(severity, n), '{}') FROM (
                       SELECT f.severity, count(*) n FROM zentriq.cloud_findings f JOIN live r ON r.id = f.resource_id
                        WHERE f.resolved_at IS NULL AND NOT f.ignored GROUP BY f.severity) x),
                  'topFindings', (SELECT COALESCE(json_agg(x), '[]') FROM (
                       SELECT f.rule, max(f.title) AS title, count(*) n,
                              (ARRAY['info','low','medium','high','critical'])[max(array_position(ARRAY['info','low','medium','high','critical'], f.severity))] AS severity,
                              max(array_position(ARRAY['info','low','medium','high','critical'], f.severity)) AS rank
                         FROM zentriq.cloud_findings f JOIN live r ON r.id = f.resource_id
                        WHERE f.resolved_at IS NULL AND NOT f.ignored GROUP BY f.rule ORDER BY 5 DESC, 3 DESC LIMIT 8) x),
                  'changes7', (SELECT COALESCE(json_object_agg(change, n), '{}') FROM (
                       SELECT change, count(*) n FROM zentriq.cloud_changes WHERE at > now() - interval '7 days' GROUP BY change) x),
                  'recentChanges', (SELECT COALESCE(json_agg(x), '[]') FROM (
                       SELECT h.id, h.at, h.change, h.detail, r.id AS "resourceId", r.name, r.kind, r.provider, c.name AS account
                         FROM zentriq.cloud_changes h JOIN zentriq.cloud_resources r ON r.id = h.resource_id JOIN zentriq.cloud_accounts c ON c.id = r.account_id
                        ORDER BY h.at DESC, h.id DESC LIMIT 10) x),
                  'unlinkedVms', (SELECT count(*) FROM live WHERE kind = 'vm' AND asset_id IS NULL AND lower(state) IN {{RunningStates}}),
                  'idleCost', (SELECT count(*) FROM zentriq.cloud_findings f JOIN live r ON r.id = f.resource_id
                                WHERE f.resolved_at IS NULL AND NOT f.ignored AND f.rule IN ('disk_unattached','ip_idle','vm_off','vm_stopped_billed','snapshot_old')),
                  'lastSync', (SELECT max(last_sync_at) FROM zentriq.cloud_accounts))::text
                """);
            return Results.Ok(await JsonAsync(cmd, ct));
        });

        // ---- connections -------------------------------------------------------------------------------------------
        g.MapGet("/accounts", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT COALESCE(json_agg(x ORDER BY lower(x.name)), '[]')::text FROM (
                  SELECT c.id, c.name, c.provider, c.settings, c.secret_protected IS NOT NULL AS "hasSecret", c.credential_id AS "credentialId",
                         (SELECT k.name FROM zentriq.credentials k WHERE k.id = c.credential_id) AS "credentialName",
                         c.enabled, c.sync_hours AS "syncHours", c.last_sync_at AS "lastSyncAt", c.last_status AS "lastStatus", c.last_error AS "lastError",
                         c.last_error_at AS "lastErrorAt", c.last_counts AS "lastCounts", c.last_warnings AS "lastWarnings", c.created_at AS "createdAt",
                         (SELECT count(*) FROM zentriq.cloud_resources r WHERE r.account_id = c.id AND r.deleted_at IS NULL) AS resources,
                         (SELECT j.status FROM zentriq.jobs j WHERE j.job_type = 'cloud.sync' AND j.payload->>'accountId' = c.id::text ORDER BY j.created_at DESC LIMIT 1) AS "jobStatus",
                         (SELECT j.id FROM zentriq.jobs j WHERE j.job_type = 'cloud.sync' AND j.payload->>'accountId' = c.id::text ORDER BY j.created_at DESC LIMIT 1) AS "jobId"
                    FROM zentriq.cloud_accounts c) x
                """);
            return Results.Ok(await JsonAsync(cmd, ct));
        });

        g.MapPost("/accounts", async (AccountBody body, NpgsqlDataSource ds, CloudStore store, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            var err = await ValidateAsync(body, true, ds, ct);
            if (err is not null) return Validation.Problem(err.Value.Field, err.Value.Message);
            var id = Guid.NewGuid();
            await using var cmd = ds.CreateCommand("""
                INSERT INTO zentriq.cloud_accounts (id, name, provider, settings, secret_protected, credential_id, enabled, sync_hours, created_by)
                VALUES (@id, @n, @p, @s, @sec, @cred, @en, @h, @by)
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", body.Name.Trim());
            cmd.Parameters.AddWithValue("p", body.Provider);
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Jsonb) { Value = Settings(body).ToJson() });
            cmd.Parameters.Add(T("sec", string.IsNullOrEmpty(body.Secret) ? null : store.ProtectSecret(id, body.Secret)));
            cmd.Parameters.Add(U("cred", body.Provider == CloudProviders.HyperV ? body.CredentialId : null));
            cmd.Parameters.AddWithValue("en", body.Enabled ?? true);
            cmd.Parameters.AddWithValue("h", Math.Clamp(body.SyncHours ?? 6, 1, 168));
            cmd.Parameters.Add(T("by", rc.UserName));
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A connection with this name already exists."); }
            await rc.AuditAsync("cloud.account_create", body.Name, detail: new { body.Provider, body.TenantId, body.ClientId, body.AccessKeyId, body.Host, body.Username, body.Hosts });
            Guid? jobId = null;
            if (body.Enabled ?? true) jobId = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.CloudSync, new { accountId = id }, MaxAttempts: 1, TimeoutSeconds: 3600, CreatedBy: rc.UserId), ct: ct);
            return Results.Created($"/api/v1/cloud/accounts/{id}", new { id, jobId });
        }).RequireAuthorization(Permissions.CloudManage);

        g.MapPut("/accounts/{id:guid}", async (Guid id, AccountBody body, NpgsqlDataSource ds, CloudStore store, RequestContext rc, CancellationToken ct) =>
        {
            await using (var chk = ds.CreateCommand("SELECT provider FROM zentriq.cloud_accounts WHERE id = @id"))
            {
                chk.Parameters.AddWithValue("id", id);
                var provider = await chk.ExecuteScalarAsync(ct) as string;
                if (provider is null) return Results.NotFound();
                if (provider != body.Provider) return Validation.Problem("provider", "The platform of a connection cannot be changed. Add a new connection instead.");
            }
            var err = await ValidateAsync(body, false, ds, ct);
            if (err is not null) return Validation.Problem(err.Value.Field, err.Value.Message);
            var newSecret = !string.IsNullOrEmpty(body.Secret);
            await using var cmd = ds.CreateCommand($"""
                UPDATE zentriq.cloud_accounts SET name = @n,
                       settings = @s || CASE WHEN @keepEmail THEN jsonb_strip_nulls(jsonb_build_object('clientEmail', settings->'clientEmail')) ELSE jsonb_build_object() END,
                       credential_id = @cred, enabled = @en, sync_hours = @h, updated_at = now()
                       {(newSecret ? ", secret_protected = @sec" : "")}
                 WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", body.Name.Trim());
            cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Jsonb) { Value = Settings(body).ToJson() });
            if (newSecret) cmd.Parameters.AddWithValue("sec", store.ProtectSecret(id, body.Secret!));
            cmd.Parameters.AddWithValue("keepEmail", body.Provider == CloudProviders.Gcp && !newSecret);
            cmd.Parameters.Add(U("cred", body.Provider == CloudProviders.HyperV ? body.CredentialId : null));
            cmd.Parameters.AddWithValue("en", body.Enabled ?? true);
            cmd.Parameters.AddWithValue("h", Math.Clamp(body.SyncHours ?? 6, 1, 168));
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A connection with this name already exists."); }
            await rc.AuditAsync("cloud.account_update", body.Name, detail: new { body.Provider, body.TenantId, body.ClientId, body.AccessKeyId, body.Host, body.Username, body.Hosts, secretChanged = newSecret });
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.CloudManage);

        g.MapDelete("/accounts/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.cloud_accounts WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            if (await cmd.ExecuteScalarAsync(ct) is not string name) return Results.NotFound();
            await rc.AuditAsync("cloud.account_delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.CloudManage);

        g.MapPost("/accounts/{id:guid}/sync", async (Guid id, NpgsqlDataSource ds, JobQueue jobs, RequestContext rc, CancellationToken ct) =>
        {
            await using var check = ds.CreateCommand("""
                SELECT c.name, EXISTS (SELECT 1 FROM zentriq.jobs j WHERE j.job_type = 'cloud.sync' AND j.status IN ('queued','running')
                                         AND j.payload->>'accountId' = c.id::text)
                  FROM zentriq.cloud_accounts c WHERE c.id = @id
                """);
            check.Parameters.AddWithValue("id", id);
            string name;
            await using (var r = await check.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                name = r.GetString(0);
                if (r.GetBoolean(1)) return Results.Conflict(new { detail = "A sync of this connection is already queued or running." });
            }
            var jobId = await jobs.EnqueueAsync(new EnqueueRequest(JobTypes.CloudSync, new { accountId = id }, MaxAttempts: 1, TimeoutSeconds: 3600, CreatedBy: rc.UserId), ct: ct);
            await rc.AuditAsync("cloud.sync_requested", name);
            return Results.Accepted($"/api/v1/jobs/{jobId}", new { jobId });
        }).RequireAuthorization(Permissions.CloudManage);

        // ---- resources -----------------------------------------------------------------------------------------------
        g.MapGet("/resources", async (string? provider, string? kind, Guid? accountId, string? state, string? q, string? linked, bool? deleted, bool? findings,
            string? parent, string? sort, int? page, int? pageSize, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 50, 1, 500);
            var offset = (Math.Max(page ?? 1, 1) - 1) * size;
            var kinds = string.IsNullOrWhiteSpace(kind) ? null : kind.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var order = sort switch
            {
                "kind" => "r.kind, lower(r.name)", "state" => "lower(r.state), lower(r.name)", "provider" => "r.provider, lower(r.name)",
                "region" => "r.region NULLS LAST, lower(r.name)", "lastSeen" => "r.last_seen_at DESC", "findings" => "(SELECT count(*) FROM zentriq.cloud_findings f WHERE f.resource_id = r.id AND f.resolved_at IS NULL AND NOT f.ignored) DESC, lower(r.name)",
                "size" => "r.size NULLS LAST, lower(r.name)", _ => "lower(r.name) NULLS LAST",
            };
            const string where = $"""
                WHERE (@p::text IS NULL OR r.provider = @p) AND (@k::text[] IS NULL OR r.kind = ANY (@k)) AND (@acc::uuid IS NULL OR r.account_id = @acc)
                  AND (CASE WHEN @del THEN r.deleted_at IS NOT NULL ELSE r.deleted_at IS NULL END)
                  AND (@st::text IS NULL OR (@st = 'running' AND lower(r.state) IN {RunningStates}) OR (@st = 'stopped' AND NOT lower(COALESCE(r.state, '')) IN {RunningStates}) OR lower(r.state) = lower(@st))
                  AND (@lnk::text IS NULL OR (@lnk = 'yes' AND r.asset_id IS NOT NULL) OR (@lnk = 'no' AND r.asset_id IS NULL))
                  AND (@par::text IS NULL OR r.parent_id = @par)
                  AND (NOT @fnd OR EXISTS (SELECT 1 FROM zentriq.cloud_findings f WHERE f.resource_id = r.id AND f.resolved_at IS NULL AND NOT f.ignored))
                  AND (@q::text IS NULL OR r.name ILIKE @q OR r.external_id ILIKE @q OR r.hostname ILIKE @q OR r.private_ip ILIKE @q OR r.public_ip ILIKE @q
                       OR r.os ILIKE @q OR r.region ILIKE @q OR r.resource_group ILIKE @q OR r.size ILIKE @q OR r.tags::text ILIKE @q)
                """;
            void P(NpgsqlCommand cmd)
            {
                cmd.Parameters.Add(T("p", provider));
                cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (object?)kinds ?? DBNull.Value });
                cmd.Parameters.Add(U("acc", accountId));
                cmd.Parameters.AddWithValue("del", deleted ?? false);
                cmd.Parameters.Add(T("st", state));
                cmd.Parameters.Add(T("lnk", linked));
                cmd.Parameters.Add(T("par", parent));
                cmd.Parameters.AddWithValue("fnd", findings ?? false);
                cmd.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%"));
            }
            await using var count = ds.CreateCommand($"SELECT count(*) FROM zentriq.cloud_resources r {where}");
            P(count);
            var total = (long)(await count.ExecuteScalarAsync(ct))!;
            await using var cmd = ds.CreateCommand($"""
                SELECT COALESCE(json_agg(x.j ORDER BY x.o), '[]')::text FROM (
                  SELECT {ResourceJson} AS j, row_number() OVER (ORDER BY {order}) AS o
                    FROM {ResourceFrom} {where}
                   ORDER BY {order} LIMIT @lim OFFSET @off) x
                """);
            P(cmd);
            cmd.Parameters.AddWithValue("lim", size);
            cmd.Parameters.AddWithValue("off", offset);
            return Results.Ok(new { items = await JsonAsync(cmd, ct), total, page = Math.Max(page ?? 1, 1), pageSize = size });
        });

        g.MapGet("/resources/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand($"""
                SELECT json_build_object(
                  'resource', {ResourceJson},
                  'accountProvider', c.provider,
                  'asset', (SELECT json_build_object('id', x.id, 'hostname', x.hostname, 'ip', COALESCE(zentriq.asset_ipv4(x.id), host(x.primary_ip)), 'os', x.os_name,
                              'lastSeen', x.last_seen_at, 'serial', x.serial_number) FROM zentriq.assets x WHERE x.id = r.asset_id),
                  'parent', (SELECT json_build_object('id', p.id, 'name', p.name, 'kind', p.kind) FROM zentriq.cloud_resources p
                              WHERE p.account_id = r.account_id AND p.external_id = r.parent_id AND p.deleted_at IS NULL),
                  'children', (SELECT COALESCE(json_agg(json_build_object('id', ch.id, 'name', ch.name, 'kind', ch.kind, 'state', ch.state, 'size', ch.size,
                                  'os', ch.os, 'privateIp', ch.private_ip, 'diskGb', ch.disk_gb, 'cpu', ch.cpu, 'memoryMb', ch.memory_mb, 'assetId', ch.asset_id)
                                  ORDER BY ch.kind, lower(ch.name)), '[]')
                                 FROM zentriq.cloud_resources ch WHERE ch.account_id = r.account_id AND ch.parent_id = r.external_id AND ch.deleted_at IS NULL),
                  'findings', (SELECT COALESCE(json_agg(json_build_object('id', f.id, 'rule', f.rule, 'severity', f.severity, 'title', f.title, 'detail', f.detail,
                                  'firstSeen', f.first_seen_at, 'lastSeen', f.last_seen_at, 'resolvedAt', f.resolved_at, 'ignored', f.ignored, 'ignoredBy', f.ignored_by)
                                  ORDER BY f.resolved_at DESC NULLS FIRST, array_position(ARRAY['critical','high','medium','low','info'], f.severity)), '[]')
                                 FROM zentriq.cloud_findings f WHERE f.resource_id = r.id),
                  'changes', (SELECT COALESCE(json_agg(json_build_object('id', h.id, 'at', h.at, 'change', h.change, 'detail', h.detail) ORDER BY h.at DESC, h.id DESC), '[]')
                                FROM (SELECT * FROM zentriq.cloud_changes h WHERE h.resource_id = r.id ORDER BY h.at DESC, h.id DESC LIMIT 100) h))::text
                  FROM {ResourceFrom} WHERE r.id = @id
                """);
            cmd.Parameters.AddWithValue("id", id);
            var json = await cmd.ExecuteScalarAsync(ct) as string;
            return json is null ? Results.NotFound() : Results.Ok(JsonDocument.Parse(json).RootElement.Clone());
        });

        // Link a machine to an inventoried device by hand, unlink it, or give it back to automatic matching.
        g.MapPut("/resources/{id:guid}/link", async (Guid id, LinkBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (body.AssetId is { } aid)
            {
                await using var a = ds.CreateCommand("SELECT EXISTS (SELECT 1 FROM zentriq.assets WHERE id = @a AND status = 'active')");
                a.Parameters.AddWithValue("a", aid);
                if (await a.ExecuteScalarAsync(ct) is not true) return Validation.Problem("assetId", "Choose an active device from the inventory.");
            }
            var auto = body.Automatic == true;
            await using var cmd = ds.CreateCommand("""
                UPDATE zentriq.cloud_resources SET asset_id = CASE WHEN @auto THEN asset_id ELSE @a END, asset_link_manual = NOT @auto
                 WHERE id = @id AND kind IN ('vm','host') RETURNING account_id, name
                """);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.Add(U("a", body.AssetId));
            cmd.Parameters.AddWithValue("auto", auto);
            Guid account; string? name;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                account = r.GetGuid(0);
                name = r.IsDBNull(1) ? null : r.GetString(1);
            }
            if (auto)
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using var link = new NpgsqlCommand(CloudStore.LinkSql, conn, tx);
                link.Parameters.AddWithValue("a", account);
                await link.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
            }
            await rc.AuditAsync("cloud.resource_link", name, detail: new { assetId = body.AssetId, automatic = auto });
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.CloudManage);

        // ---- findings and changes ---------------------------------------------------------------------------------
        g.MapGet("/findings", async (string? status, string? severity, string? provider, string? rule, Guid? accountId, string? q, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT COALESCE(json_agg(x ORDER BY x.rank DESC, x."firstSeen" DESC), '[]')::text FROM (
                  SELECT f.id, f.rule, f.severity, f.title, f.detail, f.first_seen_at AS "firstSeen", f.last_seen_at AS "lastSeen", f.resolved_at AS "resolvedAt",
                         f.ignored, f.ignored_by AS "ignoredBy", array_position(ARRAY['info','low','medium','high','critical'], f.severity) AS rank,
                         r.id AS "resourceId", r.name AS resource, r.kind, r.provider, r.region, c.name AS account, r.asset_id AS "assetId"
                    FROM zentriq.cloud_findings f JOIN zentriq.cloud_resources r ON r.id = f.resource_id JOIN zentriq.cloud_accounts c ON c.id = r.account_id
                   WHERE (CASE COALESCE(@st, 'open') WHEN 'open' THEN f.resolved_at IS NULL AND NOT f.ignored AND r.deleted_at IS NULL
                                                    WHEN 'ignored' THEN f.ignored AND f.resolved_at IS NULL
                                                    WHEN 'resolved' THEN f.resolved_at IS NOT NULL ELSE true END)
                     AND (@sev::text IS NULL OR f.severity = @sev) AND (@p::text IS NULL OR r.provider = @p) AND (@rule::text IS NULL OR f.rule = @rule)
                     AND (@acc::uuid IS NULL OR r.account_id = @acc)
                     AND (@q::text IS NULL OR r.name ILIKE @q OR f.title ILIKE @q OR f.detail ILIKE @q OR c.name ILIKE @q)
                   ORDER BY rank DESC, f.first_seen_at DESC LIMIT 2000) x
                """);
            cmd.Parameters.Add(T("st", status));
            cmd.Parameters.Add(T("sev", severity));
            cmd.Parameters.Add(T("p", provider));
            cmd.Parameters.Add(T("rule", rule));
            cmd.Parameters.Add(U("acc", accountId));
            cmd.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%"));
            return Results.Ok(await JsonAsync(cmd, ct));
        });

        g.MapPost("/findings/{id:long}/ignore", async (long id, IgnoreBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("UPDATE zentriq.cloud_findings SET ignored = @i, ignored_by = CASE WHEN @i THEN @by END WHERE id = @id RETURNING title");
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("i", body.Ignored);
            cmd.Parameters.AddWithValue("by", rc.UserName);
            if (await cmd.ExecuteScalarAsync(ct) is not string title) return Results.NotFound();
            await rc.AuditAsync(body.Ignored ? "cloud.finding_ignore" : "cloud.finding_unignore", title, detail: new { id });
            return Results.Ok(new { id, ignored = body.Ignored });
        }).RequireAuthorization(Permissions.CloudManage);

        g.MapGet("/changes", async (int? days, Guid? accountId, string? change, string? kind, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT COALESCE(json_agg(x ORDER BY x.at DESC, x.id DESC), '[]')::text FROM (
                  SELECT h.id, h.at, h.change, h.detail, r.id AS "resourceId", r.name, r.kind, r.provider, r.region, c.name AS account
                    FROM zentriq.cloud_changes h JOIN zentriq.cloud_resources r ON r.id = h.resource_id JOIN zentriq.cloud_accounts c ON c.id = r.account_id
                   WHERE h.at > now() - make_interval(days => @d) AND (@acc::uuid IS NULL OR r.account_id = @acc)
                     AND (@c::text IS NULL OR h.change = @c) AND (@k::text IS NULL OR r.kind = @k)
                   ORDER BY h.at DESC, h.id DESC LIMIT 1000) x
                """);
            cmd.Parameters.AddWithValue("d", Math.Clamp(days ?? 30, 1, 3650));
            cmd.Parameters.Add(U("acc", accountId));
            cmd.Parameters.Add(T("c", change));
            cmd.Parameters.Add(T("k", kind));
            return Results.Ok(await JsonAsync(cmd, ct));
        });
    }

    private static CloudSettings Settings(AccountBody b)
    {
        static List<string> L(string[]? a) => (a ?? []).SelectMany(x => x.Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return b.Provider switch
        {
            CloudProviders.Azure => new CloudSettings { TenantId = b.TenantId?.Trim(), ClientId = b.ClientId?.Trim(), Subscriptions = L(b.Subscriptions) },
            CloudProviders.Aws => new CloudSettings { AccessKeyId = b.AccessKeyId?.Trim(), Regions = L(b.Regions).Select(r => r.ToLowerInvariant()).ToList() },
            CloudProviders.Gcp => new CloudSettings { Projects = L(b.Projects), ClientEmail = GcpCollector.ParseKey(b.Secret)?.ClientEmail },
            CloudProviders.VMware or CloudProviders.Nutanix or CloudProviders.Proxmox or CloudProviders.XenServer or CloudProviders.Ovirt =>
                new CloudSettings { Host = b.Host?.Trim(), Port = b.Port is > 0 and < 65536 ? b.Port : null, Username = b.Username?.Trim(), SkipCertificateCheck = b.SkipCertificateCheck ?? false },
            _ => new CloudSettings { Hosts = L(b.Hosts) },
        };
    }

    private static readonly Regex GuidLike = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");

    internal static async Task<(string Field, string Message)?> ValidateAsync(AccountBody b, bool create, NpgsqlDataSource ds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 200) return ("name", "Enter a name (max 200 characters).");
        if (!CloudProviders.All.Contains(b.Provider)) return ("provider", "Choose a platform: Azure, AWS, Google Cloud, VMware vCenter, Hyper-V, Nutanix, Proxmox VE, XCP-ng / Citrix Hypervisor or oVirt / RHV.");
        if (b.SyncHours is < 1 or > 168) return ("syncHours", "Sync every 1 to 168 hours.");
        var needSecret = create && b.Provider != CloudProviders.HyperV;
        var s = Settings(b);
        switch (b.Provider)
        {
            case CloudProviders.Azure:
                if (string.IsNullOrWhiteSpace(s.TenantId) || !(GuidLike.IsMatch(s.TenantId) || Regex.IsMatch(s.TenantId, @"^[A-Za-z0-9.-]+\.[A-Za-z]{2,}$")))
                    return ("tenantId", "Enter the Directory (tenant) ID, e.g. 72f988bf-86f1-41af-91ab-2d7cd011db47 (from the app registration's Overview).");
                if (string.IsNullOrWhiteSpace(s.ClientId) || !GuidLike.IsMatch(s.ClientId)) return ("clientId", "Enter the Application (client) ID of the app registration.");
                if (s.Subscriptions.FirstOrDefault(x => !GuidLike.IsMatch(x)) is { } bad) return ("subscriptions", $"'{bad}' is not a subscription ID. Leave empty for every subscription the app can read.");
                if (needSecret && string.IsNullOrEmpty(b.Secret)) return ("secret", "Enter the client secret value.");
                break;
            case CloudProviders.Aws:
                if (string.IsNullOrWhiteSpace(s.AccessKeyId) || !Regex.IsMatch(s.AccessKeyId, "^A[KS]IA[A-Z0-9]{12,20}$")) return ("accessKeyId", "Enter the access key ID (starts with AKIA).");
                if (s.Regions.FirstOrDefault(x => !Regex.IsMatch(x, @"^[a-z]{2}(-[a-z]+)+-\d$")) is { } badRegion) return ("regions", $"'{badRegion}' is not an AWS region (for example ap-south-1). Leave empty for every enabled region.");
                if (needSecret && string.IsNullOrEmpty(b.Secret)) return ("secret", "Enter the secret access key.");
                break;
            case CloudProviders.Gcp:
                if (s.Projects.FirstOrDefault(x => !Regex.IsMatch(x, "^[a-z][a-z0-9-]{4,28}[a-z0-9]$")) is { } badProject) return ("projects", $"'{badProject}' is not a project ID (for example contoso-prod-123456). Leave empty for every project the key can see.");
                if (!string.IsNullOrEmpty(b.Secret) && GcpCollector.ParseKey(b.Secret) is null) return ("secret", "Paste the whole JSON key file of a service account (it contains \"type\": \"service_account\", client_email and private_key).");
                if (needSecret && string.IsNullOrEmpty(b.Secret)) return ("secret", "Paste the service account's JSON key file.");
                break;
            case CloudProviders.VMware or CloudProviders.Nutanix or CloudProviders.Proxmox or CloudProviders.XenServer or CloudProviders.Ovirt:
            {
                var (what, userHint) = b.Provider switch
                {
                    CloudProviders.Nutanix => ("Prism Central or Prism Element", "admin or a Prism user with the Viewer role"),
                    CloudProviders.Proxmox => ("Proxmox VE", "the API token ID, e.g. zentriq@pve!inventory"),
                    CloudProviders.XenServer => ("pool master", "root or a pool user with the Read Only role"),
                    CloudProviders.Ovirt => ("oVirt / RHV engine", "e.g. zentriq@internal"),
                    _ => ("vCenter", "e.g. zentriq@vsphere.local or CORP\\svc-zentriq"),
                };
                if (string.IsNullOrWhiteSpace(s.Host) || s.Host.Any(char.IsWhiteSpace) || s.Host.Contains('/')) return ("host", $"Enter the {what} host name or IP address.");
                if (b.Port is { } p && (p < 1 || p > 65535)) return ("port", "Port must be 1-65535.");
                if (string.IsNullOrWhiteSpace(s.Username)) return ("username", $"Enter the user — {userHint}.");
                if (b.Provider == CloudProviders.Proxmox && !Regex.IsMatch(s.Username, @"^[^@\s]+@[^!\s]+![\w.-]+$")) return ("username", "Enter the API token ID in the form user@realm!tokenname, for example zentriq@pve!inventory.");
                if (needSecret && string.IsNullOrEmpty(b.Secret)) return ("secret", b.Provider == CloudProviders.Proxmox ? "Enter the API token secret." : "Enter the password.");
                break;
            }
            case CloudProviders.HyperV:
                if (s.Hosts.Count == 0) return ("hosts", "Enter at least one Hyper-V host name or IP address (each node of a cluster).");
                if (s.Hosts.Count > 64) return ("hosts", "At most 64 hosts per connection.");
                if (s.Hosts.FirstOrDefault(h => h.Length > 255 || h.Contains('/')) is { } badHost) return ("hosts", $"'{badHost}' is not a host name or IP address.");
                if (b.CredentialId is not { } cid) return ("credentialId", "Choose the saved Windows credential used to read the hosts.");
                await using (var cmd = ds.CreateCommand("SELECT kind FROM zentriq.credentials WHERE id = @id"))
                {
                    cmd.Parameters.AddWithValue("id", cid);
                    var kind = await cmd.ExecuteScalarAsync(ct) as string;
                    if (kind is null) return ("credentialId", "The credential does not exist.");
                    if (kind is not ("windows_domain" or "windows_local")) return ("credentialId", "Choose a Windows credential (domain or local account).");
                }
                break;
        }
        return null;
    }
}
