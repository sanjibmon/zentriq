using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Licenses;
using Zentriq.Infrastructure.WebApps;
using static Zentriq.Api.Endpoints.LicenseEndpoints;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Web Apps &amp; Accounts: the web (SaaS) applications the organisation uses — found on devices (DNS cache at inventory),
/// read from Microsoft Entra ID single sign-on and Microsoft 365, or added by hand — with their status (sanctioned,
/// under review, unsanctioned, blocked), owner, cost and renewal, and their accounts (imported, synced or entered):
/// accounts of people who left or are disabled, inactive accounts, administrators.
/// </summary>
public static class WebAppEndpoints
{
    public sealed record AppBody(string? Name, string? Vendor, string? Category, string? Url, string[]? Domains, string? Status, string? Owner, Guid? DepartmentId,
        bool? Sso, decimal? CostPerUser, string? Currency, DateOnly? RenewalDate, string? Notes);
    public sealed record StatusBody(Guid[]? Ids, string? Status);
    public sealed record CatalogAddBody(string[]? Keys, string? Status);
    public sealed record AccountBody(string? UserName, string? DisplayName, string? Email, string? Role, string? Status, DateTimeOffset? LastLogin, string? LicenseTier,
        decimal? Cost, string? Department, string? Notes);
    public sealed record ImportBody(string? Csv, bool? Replace);

    private static readonly string[] Statuses = ["sanctioned", "review", "unsanctioned", "blocked"];
    private static readonly string[] Roles = ["owner", "admin", "user", "guest", "service"];
    private static readonly string[] AccountStatuses = ["active", "disabled", "invited", "removed"];

    private const string AppSelect = """
        SELECT w.id AS "id", w.catalog_key AS "catalogKey", w.name AS "name", w.vendor AS "vendor", w.category AS "category", w.url AS "url", w.domains AS "domains",
               w.status AS "status", w.owner AS "owner", w.department_id AS "departmentId", dep.name AS "department", w.sso AS "sso", w.entra_app_id AS "entraAppId",
               w.cost_per_user AS "costPerUser", w.currency AS "currency", w.renewal_date AS "renewalDate", w.notes AS "notes", w.source AS "source",
               w.first_seen_at AS "firstSeenAt", w.created_at AS "createdAt", w.updated_at AS "updatedAt",
               (SELECT count(DISTINCT u.asset_id) FROM zentriq.webapp_usage u WHERE u.app_id = w.id AND u.last_seen > now() - interval '30 days') AS "devices30d",
               (SELECT count(DISTINCT lower(u.user_name)) FROM zentriq.webapp_usage u WHERE u.app_id = w.id AND u.user_name <> '' AND u.last_seen > now() - interval '30 days') AS "users30d",
               (SELECT max(u.last_seen) FROM zentriq.webapp_usage u WHERE u.app_id = w.id) AS "lastUsed",
               (SELECT count(*) FROM accx a WHERE a.app_id = w.id AND a.status IN ('active','invited')) AS "accounts",
               (SELECT count(*) FROM accx a WHERE a.app_id = w.id AND a.orphaned) AS "orphaned",
               (SELECT count(*) FROM accx a WHERE a.app_id = w.id AND a.inactive) AS "inactive",
               (SELECT count(*) FROM accx a WHERE a.app_id = w.id AND a.status = 'active' AND a.role IN ('owner','admin')) AS "admins",
               (SELECT sum(COALESCE(a.cost, w.cost_per_user)) FROM accx a WHERE a.app_id = w.id AND a.status IN ('active','invited')) AS "cost",
               (SELECT p.id FROM zentriq.sam_products p WHERE p.webapp_id = w.id LIMIT 1) AS "productId"
          FROM zentriq.webapps w LEFT JOIN zentriq.departments dep ON dep.id = w.department_id
        """;

    private const string AccountSelect = """
        SELECT a.id AS "id", a.app_id AS "appId", a.app_name AS "app", a.user_name AS "userName", a.display_name AS "displayName", a.email AS "email", a.role AS "role",
               a.status AS "status", a.source AS "source", a.last_login AS "lastLogin", a.directory_sign_in AS "directorySignIn", a.last_used AS "lastUsed",
               a.last_activity AS "lastActivity", a.license_tier AS "licenseTier", a.cost AS "cost", COALESCE(a.department, a.directory_department) AS "department",
               a.notes AS "notes", a.orphaned AS "orphaned", a.orphan_reason AS "orphanReason", a.inactive AS "inactive", a.directory_enabled AS "directoryEnabled",
               a.person_left AS "personLeft", a.created_at AS "createdAt", a.synced_at AS "syncedAt"
          FROM accx a
        """;

    public static void MapWebAppEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/webapps").WithTags("Web apps and accounts").RequireModule(Modules.WebApps).RequireAuthorization(Permissions.WebAppsView);

        g.MapGet("/summary", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, $$"""
                WITH {{WebAppQueries.AccountsCte}}
                SELECT jsonb_build_object(
                  'apps', (SELECT count(*) FROM zentriq.webapps),
                  'byStatus', (SELECT COALESCE(jsonb_object_agg(status, n), '{}') FROM (SELECT status, count(*) n FROM zentriq.webapps GROUP BY 1) x),
                  'byCategory', (SELECT COALESCE(jsonb_agg(jsonb_build_object('name', category, 'count', n) ORDER BY n DESC), '[]') FROM (SELECT category, count(*) n FROM zentriq.webapps GROUP BY 1 ORDER BY 2 DESC LIMIT 10) x),
                  'sso', (SELECT count(*) FROM zentriq.webapps WHERE sso),
                  'new30d', (SELECT count(*) FROM zentriq.webapps WHERE COALESCE(first_seen_at, created_at) > now() - interval '30 days'),
                  'inUse30d', (SELECT count(DISTINCT app_id) FROM zentriq.webapp_usage WHERE last_seen > now() - interval '30 days'),
                  'unsanctionedInUse', (SELECT count(DISTINCT w.id) FROM zentriq.webapps w JOIN zentriq.webapp_usage u ON u.app_id = w.id AND u.last_seen > now() - interval '7 days'
                                        WHERE w.status IN ('unsanctioned','blocked')),
                  'usersSeen30d', (SELECT count(DISTINCT lower(user_name)) FROM zentriq.webapp_usage WHERE user_name <> '' AND last_seen > now() - interval '30 days'),
                  'devicesSeen30d', (SELECT count(DISTINCT asset_id) FROM zentriq.webapp_usage WHERE last_seen > now() - interval '30 days'),
                  'accounts', (SELECT count(*) FROM accx WHERE status IN ('active','invited')),
                  'orphaned', (SELECT count(*) FROM accx WHERE orphaned),
                  'inactive', (SELECT count(*) FROM accx WHERE inactive),
                  'admins', (SELECT count(*) FROM accx WHERE status = 'active' AND role IN ('owner','admin')),
                  'cost', (SELECT COALESCE(sum(COALESCE(a.cost, w.cost_per_user)), 0) FROM accx a JOIN zentriq.webapps w ON w.id = a.app_id WHERE a.status IN ('active','invited')),
                  'inactiveCost', (SELECT COALESCE(sum(COALESCE(a.cost, w.cost_per_user)), 0) FROM accx a JOIN zentriq.webapps w ON w.id = a.app_id WHERE a.inactive),
                  'renewals', (SELECT COALESCE(jsonb_agg(jsonb_build_object('id', id, 'name', name, 'renewalDate', renewal_date, 'days', renewal_date - current_date) ORDER BY renewal_date), '[]')
                               FROM (SELECT * FROM zentriq.webapps WHERE renewal_date >= current_date - 7 ORDER BY renewal_date LIMIT 6) r),
                  'top', (SELECT COALESCE(jsonb_agg(jsonb_build_object('id', w.id, 'name', w.name, 'status', w.status, 'users', x.users, 'devices', x.devices) ORDER BY x.users DESC, x.devices DESC), '[]')
                          FROM (SELECT app_id, count(DISTINCT lower(NULLIF(user_name, ''))) users, count(DISTINCT asset_id) devices FROM zentriq.webapp_usage
                                 WHERE last_seen > now() - interval '30 days' GROUP BY 1 ORDER BY 2 DESC, 3 DESC LIMIT 8) x JOIN zentriq.webapps w ON w.id = x.app_id),
                  'alerts', (SELECT COALESCE(jsonb_object_agg(severity, n), '{}') FROM (SELECT severity, count(*) n FROM zentriq.sam_alerts
                             WHERE resolved_at IS NULL AND NOT dismissed AND kind LIKE 'webapp_%' GROUP BY 1) x),
                  'catalogVersion', @cv::text,
                  'discovery', @disc::boolean,
                  'inactiveDays', @days::int
                )::text
                """, c => { c.Parameters.AddWithValue("days", settings.InactiveDays); c.Parameters.AddWithValue("cv", WebAppCatalog.Standard.Version); c.Parameters.AddWithValue("disc", settings.WebDiscovery); }, ct);
            return Results.Text(json ?? "{}", "application/json");
        });

        g.MapGet("", async (NpgsqlDataSource ds, string? status, string? category, string? q, string? filter, CancellationToken ct) =>
        {
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, $$"""
                WITH {{WebAppQueries.AccountsCte}}
                SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."users30d" DESC, t."accounts" DESC, lower(t."name")), '[]')::text FROM ({{AppSelect}}
                 WHERE (@st::text IS NULL OR w.status = @st) AND (@cat::text IS NULL OR w.category = @cat)
                   AND (@q::text IS NULL OR w.name ILIKE '%' || @q || '%' OR w.vendor ILIKE '%' || @q || '%' OR w.owner ILIKE '%' || @q || '%' OR array_to_string(w.domains, ' ') ILIKE '%' || @q || '%')
                 LIMIT 3000) t
                 WHERE (@f::text IS NULL OR (@f = 'inuse' AND t."devices30d" > 0) OR (@f = 'orphaned' AND t."orphaned" > 0) OR (@f = 'inactive' AND t."inactive" > 0)
                        OR (@f = 'sso' AND t."sso") OR (@f = 'shadow' AND t."status" IN ('review','unsanctioned','blocked') AND t."devices30d" > 0))
                """, c =>
            {
                c.Parameters.AddWithValue("days", settings.InactiveDays);
                c.Parameters.Add(T("st", status)); c.Parameters.Add(T("cat", category)); c.Parameters.Add(T("f", filter));
                c.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : q.Trim().Replace("%", "")));
            }, ct);
            return Results.Text(json ?? "[]", "application/json");
        });

        g.MapGet("/catalog", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = ds.CreateCommand("SELECT COALESCE(catalog_key, ''), lower(name) FROM zentriq.webapps"))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) { have.Add(r.GetString(0)); have.Add(r.GetString(1)); }
            return Results.Ok(new
            {
                version = WebAppCatalog.Standard.Version,
                apps = WebAppCatalog.Standard.Apps.Where(a => !have.Contains(a.Key) && !have.Contains(a.Name.ToLowerInvariant()))
                    .OrderBy(a => a.Category).ThenBy(a => a.Name).Select(a => new { a.Key, a.Name, a.Vendor, a.Category, a.Url, a.Domains }),
            });
        });

        g.MapPost("/from-catalog", async (CatalogAddBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var status = body.Status ?? "sanctioned";
            if (!Statuses.Contains(status)) return Validation.Problem("status", "Choose the status.");
            var entries = (body.Keys ?? []).Select(k => WebAppCatalog.Standard.Find(k)).Where(e => e is not null).Select(e => e!).ToList();
            if (entries.Count == 0) return Validation.Problem("keys", "Choose web apps from the list.");
            var added = 0;
            foreach (var e in entries)
            {
                await using var cmd = ds.CreateCommand("""
                    INSERT INTO zentriq.webapps (catalog_key, name, vendor, category, url, domains, status, source, created_by)
                    VALUES (@k, @n, @v, @c, @u, @d, @s, 'catalog', @by) ON CONFLICT DO NOTHING
                    """);
                cmd.Parameters.AddWithValue("k", e.Key); cmd.Parameters.AddWithValue("n", e.Name); cmd.Parameters.Add(T("v", e.Vendor));
                cmd.Parameters.AddWithValue("c", e.Category); cmd.Parameters.Add(T("u", e.Url)); cmd.Parameters.AddWithValue("d", e.Domains.ToArray());
                cmd.Parameters.AddWithValue("s", status); cmd.Parameters.Add(T("by", rc.UserName));
                added += await cmd.ExecuteNonQueryAsync(ct);
            }
            await rc.AuditAsync("webapps.add_catalog", $"{added} app(s)", detail: new { keys = entries.Select(e => e.Key), status });
            return Results.Ok(new { added });
        }).RequireAuthorization(Permissions.WebAppsManage);

        g.MapPost("", async (AppBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var err = ValidateApp(body);
            if (err is not null) return err;
            await using var cmd = ds.CreateCommand($"INSERT INTO zentriq.webapps ({AppColumns}, source, created_by) VALUES ({AppValues}, 'manual', @by) RETURNING id");
            BindApp(cmd, body);
            cmd.Parameters.Add(T("by", rc.UserName));
            try
            {
                var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
                await rc.AuditAsync("webapps.create", body.Name, detail: body);
                return Results.Created($"/api/v1/webapps/{id}", new { id });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A web app with this name already exists."); }
        }).RequireAuthorization(Permissions.WebAppsManage);

        g.MapPut("/{id:guid}", async (Guid id, AppBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var err = ValidateApp(body);
            if (err is not null) return err;
            await using var cmd = ds.CreateCommand($"UPDATE zentriq.webapps SET ({AppColumns}) = ROW({AppValues}), updated_at = now() WHERE id = @id");
            BindApp(cmd, body);
            cmd.Parameters.AddWithValue("id", id);
            try { if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("name", "A web app with this name already exists."); }
            await rc.AuditAsync("webapps.update", body.Name, detail: body);
            return Results.Ok(new { id });
        }).RequireAuthorization(Permissions.WebAppsManage);

        g.MapPost("/status", async (StatusBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (body.Status is null || !Statuses.Contains(body.Status)) return Validation.Problem("status", "Choose the status.");
            var ids = (body.Ids ?? []).Distinct().Take(1000).ToArray();
            if (ids.Length == 0) return Validation.Problem("ids", "Choose web apps.");
            await using var cmd = ds.CreateCommand("UPDATE zentriq.webapps SET status = @s, updated_at = now() WHERE id = ANY (@ids)");
            cmd.Parameters.AddWithValue("s", body.Status);
            cmd.Parameters.AddWithValue("ids", ids);
            var n = await cmd.ExecuteNonQueryAsync(ct);
            await rc.AuditAsync("webapps.status", body.Status, detail: new { ids, body.Status });
            await LicenseEvaluator.RunAsync(ds, ct);
            return Results.Ok(new { updated = n });
        }).RequireAuthorization(Permissions.WebAppsManage);

        g.MapDelete("/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.webapps WHERE id = @id RETURNING name");
            cmd.Parameters.AddWithValue("id", id);
            var name = await cmd.ExecuteScalarAsync(ct) as string;
            if (name is null) return Results.NotFound();
            await rc.AuditAsync("webapps.delete", name);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.WebAppsManage);

        g.MapGet("/{id:guid}", async (Guid id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, $$"""
                WITH {{WebAppQueries.AccountsCte}}
                SELECT jsonb_build_object(
                  'app', (SELECT to_jsonb(t) FROM ({{AppSelect}} WHERE w.id = @id) t),
                  'users', (SELECT COALESCE(jsonb_agg(jsonb_build_object('user', u, 'devices', d, 'days', days, 'lastSeen', ls) ORDER BY ls DESC), '[]') FROM (
                        SELECT NULLIF(user_name, '') u, count(DISTINCT asset_id) d, max(days) days, max(last_seen) ls FROM zentriq.webapp_usage WHERE app_id = @id
                         GROUP BY lower(user_name), user_name ORDER BY 4 DESC LIMIT 500) x),
                  'devices', (SELECT COALESCE(jsonb_agg(jsonb_build_object('assetId', a.id, 'hostname', COALESCE(a.hostname, host(a.primary_ip)), 'user', NULLIF(u.user_name, ''),
                        'firstSeen', u.first_seen, 'lastSeen', u.last_seen, 'days', u.days, 'department', dep.name) ORDER BY u.last_seen DESC), '[]')
                        FROM zentriq.webapp_usage u JOIN zentriq.assets a ON a.id = u.asset_id LEFT JOIN zentriq.departments dep ON dep.id = a.department_id
                       WHERE u.app_id = @id),
                  'products', (SELECT COALESCE(jsonb_agg(jsonb_build_object('id', p.id, 'name', p.name)), '[]') FROM zentriq.sam_products p WHERE p.webapp_id = @id),
                  'alerts', (SELECT COALESCE(jsonb_agg(jsonb_build_object('id', s.id, 'kind', s.kind, 'severity', s.severity, 'title', s.title, 'detail', s.detail, 'firstAt', s.first_at)), '[]')
                        FROM zentriq.sam_alerts s WHERE s.app_id = @id AND s.resolved_at IS NULL AND NOT s.dismissed)
                )::text
                """, c => { c.Parameters.AddWithValue("id", id); c.Parameters.AddWithValue("days", settings.InactiveDays); }, ct);
            return json is null || json.Contains("\"app\": null") ? Results.NotFound() : Results.Text(json, "application/json");
        });

        // Accounts of one app, or of every app (appId omitted).
        g.MapGet("/accounts", async (NpgsqlDataSource ds, Guid? appId, string? filter, string? q, CancellationToken ct) =>
        {
            var settings = await LicenseSettings.LoadAsync(ds, ct);
            await using var conn = await ds.OpenConnectionAsync(ct);
            var json = await ScalarAsync(conn, $$"""
                WITH {{WebAppQueries.AccountsCte}}
                SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY t."orphaned" DESC, t."inactive" DESC, lower(t."userName")), '[]')::text FROM ({{AccountSelect}}
                 WHERE (@app::uuid IS NULL OR a.app_id = @app)
                   AND (@f::text IS NULL OR (@f = 'orphaned' AND a.orphaned) OR (@f = 'inactive' AND a.inactive) OR (@f = 'admins' AND a.role IN ('owner','admin') AND a.status = 'active')
                        OR (@f = 'active' AND a.status = 'active') OR (@f = 'disabled' AND a.status IN ('disabled','removed')) OR (@f = 'guests' AND a.role = 'guest'))
                   AND (@q::text IS NULL OR a.user_name ILIKE '%' || @q || '%' OR a.display_name ILIKE '%' || @q || '%' OR a.email ILIKE '%' || @q || '%' OR a.app_name ILIKE '%' || @q || '%')
                 LIMIT 10000) t
                """, c =>
            {
                c.Parameters.AddWithValue("days", settings.InactiveDays);
                c.Parameters.Add(new NpgsqlParameter("app", NpgsqlDbType.Uuid) { Value = (object?)appId ?? DBNull.Value });
                c.Parameters.Add(T("f", filter));
                c.Parameters.Add(T("q", string.IsNullOrWhiteSpace(q) ? null : q.Trim().Replace("%", "")));
            }, ct);
            return Results.Text(json ?? "[]", "application/json");
        });

        g.MapPost("/{id:guid}/accounts", async (Guid id, AccountBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var err = ValidateAccount(body);
            if (err is not null) return err;
            await using var cmd = ds.CreateCommand($"INSERT INTO zentriq.webapp_accounts (app_id, {AccountColumns}, source) VALUES (@app, {AccountValues}, 'manual') RETURNING id");
            cmd.Parameters.AddWithValue("app", id);
            BindAccount(cmd, body);
            try
            {
                var aid = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
                await rc.AuditAsync("webapps.account_create", body.UserName, detail: new { appId = id, body.Role, body.Status });
                return Results.Created($"/api/v1/webapps/accounts/{aid}", new { id = aid });
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("userName", "This user already has an account in this app."); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation) { return Results.NotFound(); }
        }).RequireAuthorization(Permissions.WebAppsManage);

        g.MapPut("/accounts/{aid:guid}", async (Guid aid, AccountBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var err = ValidateAccount(body);
            if (err is not null) return err;
            await using var cmd = ds.CreateCommand($"UPDATE zentriq.webapp_accounts SET ({AccountColumns}) = ROW({AccountValues}), updated_at = now() WHERE id = @id");
            cmd.Parameters.AddWithValue("id", aid);
            BindAccount(cmd, body);
            try { if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { return Validation.Problem("userName", "This user already has an account in this app."); }
            await rc.AuditAsync("webapps.account_update", body.UserName, detail: new { body.Role, body.Status });
            return Results.Ok(new { id = aid });
        }).RequireAuthorization(Permissions.WebAppsManage);

        g.MapDelete("/accounts/{aid:guid}", async (Guid aid, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("DELETE FROM zentriq.webapp_accounts WHERE id = @id RETURNING user_name");
            cmd.Parameters.AddWithValue("id", aid);
            var user = await cmd.ExecuteScalarAsync(ct) as string;
            if (user is null) return Results.NotFound();
            await rc.AuditAsync("webapps.account_delete", user);
            return Results.NoContent();
        }).RequireAuthorization(Permissions.WebAppsManage);

        // CSV export of an app's user list (Google Workspace, Salesforce, Slack, Zoom … admin consoles all export one).
        g.MapPost("/{id:guid}/accounts/import", async (Guid id, ImportBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            var (rows, error) = ParseAccounts(body.Csv ?? "");
            if (error is not null) return Validation.Problem("csv", error);
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var chk = new NpgsqlCommand("SELECT 1 FROM zentriq.webapps WHERE id = @id", conn, tx))
            {
                chk.Parameters.AddWithValue("id", id);
                if (await chk.ExecuteScalarAsync(ct) is null) return Results.NotFound();
            }
            var n = 0;
            foreach (var a in rows)
            {
                await using var cmd = new NpgsqlCommand($"""
                    INSERT INTO zentriq.webapp_accounts AS w (app_id, {AccountColumns}, source, synced_at) VALUES (@app, {AccountValues}, 'import', now())
                    ON CONFLICT (app_id, lower(user_name)) DO UPDATE SET display_name = COALESCE(EXCLUDED.display_name, w.display_name), email = COALESCE(EXCLUDED.email, w.email),
                        role = EXCLUDED.role, status = EXCLUDED.status, last_login = COALESCE(EXCLUDED.last_login, w.last_login),
                        license_tier = COALESCE(EXCLUDED.license_tier, w.license_tier), cost = COALESCE(EXCLUDED.cost, w.cost),
                        department = COALESCE(EXCLUDED.department, w.department), synced_at = now(), updated_at = now()
                    """, conn, tx);
                cmd.Parameters.AddWithValue("app", id);
                BindAccount(cmd, a);
                n += await cmd.ExecuteNonQueryAsync(ct);
            }
            var removed = 0;
            if (body.Replace == true)
            {
                await using var del = new NpgsqlCommand("UPDATE zentriq.webapp_accounts SET status = 'removed', updated_at = now() WHERE app_id = @app AND source = 'import' AND status <> 'removed' AND NOT (lower(user_name) = ANY (@u))", conn, tx);
                del.Parameters.AddWithValue("app", id);
                del.Parameters.AddWithValue("u", rows.Select(r => r.UserName!.ToLowerInvariant()).ToArray());
                removed = await del.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            await rc.AuditAsync("webapps.accounts_import", id.ToString(), detail: new { imported = n, removed });
            await LicenseEvaluator.RunAsync(ds, ct);
            return Results.Ok(new { imported = n, removed });
        }).RequireAuthorization(Permissions.WebAppsManage);

        g.MapGet("/alerts", async (NpgsqlDataSource ds, string? status, string? kind, CancellationToken ct) =>
            Results.Text(await AlertsJsonAsync(ds, "webapps", status, kind, ct), "application/json"));
        g.MapPost("/alerts/{id:long}/dismiss", async (long id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => await DismissAsync(ds, rc, id, true, ct))
            .RequireAuthorization(Permissions.WebAppsManage);
        g.MapPost("/alerts/{id:long}/restore", async (long id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) => await DismissAsync(ds, rc, id, false, ct))
            .RequireAuthorization(Permissions.WebAppsManage);
    }

    private const string AppColumns = "name, vendor, category, url, domains, status, owner, department_id, sso, cost_per_user, currency, renewal_date, notes";
    private const string AppValues = "@n, @v, @c, @u, @d, @s, @o, @dep, @sso, @cpu, @cur, @rd, @no";

    private static IResult? ValidateApp(AppBody b)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 120) return Validation.Problem("name", "Enter the name of the web app (up to 120 characters).");
        if (b.Status is not null && !Statuses.Contains(b.Status)) return Validation.Problem("status", "Choose the status.");
        if ((b.Domains ?? []).Any(d => !string.IsNullOrWhiteSpace(d) && WebAppCatalog.CleanDomain(d) is null)) return Validation.Problem("domains", "Enter domain names such as app.example.com.");
        if ((b.Domains ?? []).Length > 20) return Validation.Problem("domains", "Use at most 20 domains.");
        if (b.CostPerUser is < 0) return Validation.Problem("costPerUser", "The cost cannot be negative.");
        if (b.Currency is { Length: > 0 } c && (c.Trim().Length != 3 || !c.Trim().All(char.IsLetter))) return Validation.Problem("currency", "Use a three-letter currency code.");
        return null;
    }

    private static void BindApp(NpgsqlCommand cmd, AppBody b)
    {
        cmd.Parameters.AddWithValue("n", b.Name!.Trim());
        cmd.Parameters.Add(T("v", Opt(b.Vendor, 120)));
        cmd.Parameters.AddWithValue("c", string.IsNullOrWhiteSpace(b.Category) ? "Other" : b.Category.Trim());
        cmd.Parameters.Add(T("u", Opt(b.Url, 500)));
        cmd.Parameters.AddWithValue("d", (b.Domains ?? []).Select(WebAppCatalog.CleanDomain).Where(d => d is not null).Select(d => d!).Distinct().ToArray());
        cmd.Parameters.AddWithValue("s", b.Status ?? "sanctioned");
        cmd.Parameters.Add(T("o", Opt(b.Owner, 200)));
        cmd.Parameters.Add(new NpgsqlParameter("dep", NpgsqlDbType.Uuid) { Value = (object?)b.DepartmentId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("sso", b.Sso ?? false);
        cmd.Parameters.Add(new NpgsqlParameter("cpu", NpgsqlDbType.Numeric) { Value = (object?)b.CostPerUser ?? DBNull.Value });
        cmd.Parameters.AddWithValue("cur", string.IsNullOrWhiteSpace(b.Currency) ? "USD" : b.Currency.Trim().ToUpperInvariant());
        cmd.Parameters.Add(new NpgsqlParameter("rd", NpgsqlDbType.Date) { Value = (object?)b.RenewalDate ?? DBNull.Value });
        cmd.Parameters.Add(T("no", Opt(b.Notes, 4000)));
    }

    private const string AccountColumns = "user_name, display_name, email, role, status, last_login, license_tier, cost, department, notes";
    private const string AccountValues = "@un, @dn, @em, @ro, @st, @ll, @lt, @co, @de, @no";

    private static IResult? ValidateAccount(AccountBody b)
    {
        if (string.IsNullOrWhiteSpace(b.UserName) || b.UserName.Trim().Length > 254) return Validation.Problem("userName", "Enter the user name or e-mail address of the account.");
        if (b.Role is not null && !Roles.Contains(b.Role)) return Validation.Problem("role", "Choose the role.");
        if (b.Status is not null && !AccountStatuses.Contains(b.Status)) return Validation.Problem("status", "Choose the status.");
        if (b.Cost is < 0) return Validation.Problem("cost", "The cost cannot be negative.");
        return null;
    }

    private static void BindAccount(NpgsqlCommand cmd, AccountBody b)
    {
        cmd.Parameters.AddWithValue("un", b.UserName!.Trim());
        cmd.Parameters.Add(T("dn", Opt(b.DisplayName, 200)));
        cmd.Parameters.Add(T("em", Opt(b.Email, 254)));
        cmd.Parameters.AddWithValue("ro", b.Role ?? "user");
        cmd.Parameters.AddWithValue("st", b.Status ?? "active");
        cmd.Parameters.Add(new NpgsqlParameter("ll", NpgsqlDbType.TimestampTz) { Value = (object?)b.LastLogin?.ToUniversalTime() ?? DBNull.Value });
        cmd.Parameters.Add(T("lt", Opt(b.LicenseTier, 200)));
        cmd.Parameters.Add(new NpgsqlParameter("co", NpgsqlDbType.Numeric) { Value = (object?)b.Cost ?? DBNull.Value });
        cmd.Parameters.Add(T("de", Opt(b.Department, 200)));
        cmd.Parameters.Add(T("no", Opt(b.Notes, 1000)));
    }

    private static string? Opt(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();

    /// <summary>Accounts from a CSV user export (see <see cref="AccountCsv"/>).</summary>
    public static (List<AccountBody> Rows, string? Error) ParseAccounts(string csv)
    {
        var (rows, error) = AccountCsv.Parse(csv);
        return (rows.Select(r => new AccountBody(r.UserName, r.DisplayName, r.Email, r.Role, r.Status, r.LastLogin, r.LicenseTier, r.Cost, r.Department, null)).ToList(), error);
    }

}
