using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Secrets;
using Zentriq.Infrastructure.WebApps;

namespace Zentriq.Infrastructure.Licenses;

public sealed class M365Exception(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads a Microsoft 365 / Entra ID tenant with an app registration (client credentials, Microsoft Graph application
/// permissions — read only): purchased licences and their use (subscribedSkus), subscription renewal / expiry dates
/// (directory/subscriptions), users with their licences, status and last sign-in, and the single sign-on applications
/// with the users assigned to them (Web Apps &amp; Accounts). Nothing is changed in the tenant.
/// </summary>
public sealed class M365Sync(NpgsqlDataSource ds, CredentialProtector protector, HttpMessageHandler? handler = null,
    string loginBase = "https://login.microsoftonline.com", string graphBase = "https://graph.microsoft.com")
{
    /// <summary>Microsoft's own tenant: first-party applications are not web apps of the organisation.</summary>
    private static readonly HashSet<string> MicrosoftTenants = new(StringComparer.OrdinalIgnoreCase)
        { "f8cdef31-a31e-4b4a-93e4-5f571e91255a", "72f988bf-86f1-41af-91ab-2d7cd011db47", "47df5bb7-e6bc-4256-afb0-dd8c8e3c1ce8" };

    public const int MaxUsers = 50_000;

    public static string Protect(CredentialProtector protector, Guid id, string value) => protector.Protect(id, new CredentialSecret { Password = value });

    public sealed record Outcome(int Skus, int Users, int Apps, int Accounts, string? Note);

    public async Task<Outcome> RunAsync(Guid connectionId, CancellationToken ct)
    {
        string tenant, client, name; string? prot; bool syncApps;
        await using (var cmd = ds.CreateCommand("SELECT tenant_id, client_id, client_protected, name, sync_apps FROM zentriq.sam_cloud_connections WHERE id = @id"))
        {
            cmd.Parameters.AddWithValue("id", connectionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new M365Exception("The connection no longer exists.");
            tenant = r.GetString(0); client = r.GetString(1); prot = r.IsDBNull(2) ? null : r.GetString(2); name = r.GetString(3); syncApps = r.GetBoolean(4);
        }
        if (prot is null) throw new M365Exception("The client secret value is missing. Edit the connection and enter it.");
        var credential = protector.Unprotect(connectionId, prot).Password ?? "";

        try
        {
            var outcome = await SyncAsync(connectionId, tenant, client, credential, syncApps, ct);
            await using var ok = ds.CreateCommand("UPDATE zentriq.sam_cloud_connections SET last_sync_at = now(), last_status = 'succeeded', last_error = NULL, last_note = @n WHERE id = @id");
            ok.Parameters.AddWithValue("id", connectionId);
            ok.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = (object?)outcome.Note ?? DBNull.Value });
            await ok.ExecuteNonQueryAsync(ct);
            return outcome;
        }
        catch (Exception ex) when (ex is M365Exception or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await using var bad = ds.CreateCommand("UPDATE zentriq.sam_cloud_connections SET last_sync_at = now(), last_status = 'failed', last_error = @e WHERE id = @id");
            bad.Parameters.AddWithValue("id", connectionId);
            bad.Parameters.AddWithValue("e", ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message);
            await bad.ExecuteNonQueryAsync(ct);
            throw ex as M365Exception ?? new M365Exception(ex.Message, ex);
        }
    }

    private async Task<Outcome> SyncAsync(Guid connectionId, string tenant, string client, string credential, bool syncApps, CancellationToken ct)
    {
        using var http = handler is null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials })
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(2);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zentriq", "1.1"));
        var token = await TokenAsync(http, tenant, client, credential, ct);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var notes = new List<string>();

        // ---- licences bought and assigned ---------------------------------------------------------------------------
        var skus = await GetAllAsync(http, $"{graphBase}/v1.0/subscribedSkus", 1000, ct, "Organization.Read.All");
        var lifecycle = new Dictionary<string, (DateTimeOffset? Next, bool Trial)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var s in await GetAllAsync(http, $"{graphBase}/v1.0/directory/subscriptions", 5000, ct, "Organization.Read.All"))
            {
                var sku = Str(s, "skuId"); if (sku is null) continue;
                var st = Str(s, "status");
                if (st is "Deleted") continue;
                DateTimeOffset? next = DateTimeOffset.TryParse(Str(s, "nextLifecycleDateTime"), out var n) ? n : null;
                var trial = s.TryGetProperty("isTrial", out var t) && t.ValueKind == JsonValueKind.True;
                if (lifecycle.TryGetValue(sku, out var cur))
                    lifecycle[sku] = (cur.Next is null || (next is not null && next < cur.Next) ? next : cur.Next, cur.Trial || trial);
                else lifecycle[sku] = (next, trial);
            }
        }
        catch (M365Exception ex) { notes.Add("Renewal dates not read: " + ex.Message); }

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var ids = new List<string>();
            foreach (var s in skus)
            {
                var skuId = Str(s, "skuId"); var part = Str(s, "skuPartNumber");
                if (skuId is null || part is null) continue;
                ids.Add(skuId);
                var pre = s.TryGetProperty("prepaidUnits", out var pu) ? pu : default;
                int U(string k) => pre.ValueKind == JsonValueKind.Object && pre.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : 0;
                var consumed = s.TryGetProperty("consumedUnits", out var cu) && cu.TryGetInt32(out var c) ? c : 0;
                lifecycle.TryGetValue(skuId, out var lc);
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO zentriq.sam_cloud_skus (connection_id, sku_id, sku_part, name, enabled_units, suspended, warning, consumed, status, next_lifecycle, is_trial, synced_at)
                    VALUES (@c, @id, @p, @n, @e, @s, @w, @u, @st, @nl, @tr, now())
                    ON CONFLICT (connection_id, sku_id) DO UPDATE SET sku_part = EXCLUDED.sku_part, name = EXCLUDED.name, enabled_units = EXCLUDED.enabled_units,
                        suspended = EXCLUDED.suspended, warning = EXCLUDED.warning, consumed = EXCLUDED.consumed, status = EXCLUDED.status,
                        next_lifecycle = EXCLUDED.next_lifecycle, is_trial = EXCLUDED.is_trial, synced_at = now()
                    """, conn, tx);
                cmd.Parameters.AddWithValue("c", connectionId);
                cmd.Parameters.AddWithValue("id", skuId);
                cmd.Parameters.AddWithValue("p", part);
                cmd.Parameters.AddWithValue("n", SkuNames.Name(part));
                cmd.Parameters.AddWithValue("e", U("enabled"));
                cmd.Parameters.AddWithValue("s", U("suspended"));
                cmd.Parameters.AddWithValue("w", U("warning"));
                cmd.Parameters.AddWithValue("u", consumed);
                cmd.Parameters.Add(new NpgsqlParameter("st", NpgsqlDbType.Text) { Value = (object?)Str(s, "capabilityStatus") ?? DBNull.Value });
                cmd.Parameters.Add(new NpgsqlParameter("nl", NpgsqlDbType.TimestampTz) { Value = lc.Next is { } nx ? (object)nx.ToUniversalTime() : DBNull.Value });
                cmd.Parameters.AddWithValue("tr", lc.Trial);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var del = new NpgsqlCommand("DELETE FROM zentriq.sam_cloud_skus WHERE connection_id = @c AND NOT (sku_id = ANY (@ids))", conn, tx))
            {
                del.Parameters.AddWithValue("c", connectionId);
                del.Parameters.AddWithValue("ids", ids.ToArray());
                await del.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        // ---- users --------------------------------------------------------------------------------------------------
        const string select = "id,userPrincipalName,displayName,mail,department,jobTitle,accountEnabled,assignedLicenses";
        List<JsonElement> users;
        try { users = await GetAllAsync(http, $"{graphBase}/v1.0/users?$select={select},signInActivity&$top=999", MaxUsers, ct, "User.Read.All, AuditLog.Read.All"); }
        catch (M365Exception)
        {
            // Last sign-in needs AuditLog.Read.All and Microsoft Entra ID P1; without it the rest still works.
            users = await GetAllAsync(http, $"{graphBase}/v1.0/users?$select={select}&$top=999", MaxUsers, ct, "User.Read.All");
            notes.Add("Last sign-in not available (needs AuditLog.Read.All and Microsoft Entra ID P1 or P2).");
        }
        var skuNames = skus.Where(s => Str(s, "skuId") is not null).GroupBy(s => Str(s, "skuId")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => SkuNames.Name(Str(g.First(), "skuPartNumber") ?? ""), StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, (string Upn, string? Name, string? Mail, string? Dept, bool? Enabled, DateTimeOffset? SignIn, string[] Skus)>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in users)
        {
            var id = Str(u, "id"); var upn = Str(u, "userPrincipalName");
            if (id is null || upn is null) continue;
            var lic = u.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array
                ? al.EnumerateArray().Select(x => Str(x, "skuId")).Where(x => x is not null).Select(x => x!).ToArray() : [];
            DateTimeOffset? signIn = null;
            if (u.TryGetProperty("signInActivity", out var sa) && sa.ValueKind == JsonValueKind.Object)
            {
                DateTimeOffset? Latest(params string[] keys) => keys.Select(k => DateTimeOffset.TryParse(Str(sa, k), out var d) ? d : (DateTimeOffset?)null).Max();
                signIn = Latest("lastSignInDateTime", "lastNonInteractiveSignInDateTime", "lastSuccessfulSignInDateTime");
            }
            bool? enabled = u.TryGetProperty("accountEnabled", out var ae) && ae.ValueKind is JsonValueKind.True or JsonValueKind.False ? ae.GetBoolean() : null;
            byId[id] = (upn, Str(u, "displayName"), Str(u, "mail"), Str(u, "department"), enabled, signIn, lic);
        }
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using (var del = new NpgsqlCommand("DELETE FROM zentriq.sam_cloud_users WHERE connection_id = @c", conn, tx))
            {
                del.Parameters.AddWithValue("c", connectionId);
                await del.ExecuteNonQueryAsync(ct);
            }
            foreach (var chunk in byId.Chunk(2000))
            {
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO zentriq.sam_cloud_users (connection_id, user_id, upn, display_name, mail, department, account_enabled, last_sign_in, skus, synced_at)
                    SELECT @c, x.id, x.upn, x.nm, x.ml, x.dp, x.en, x.si, COALESCE(string_to_array(NULLIF(x.sk, ''), ','), '{}'), now()
                      FROM unnest(@id, @upn, @nm, @ml, @dp, @en, @si, @sk) AS x(id, upn, nm, ml, dp, en, si, sk)
                    """, conn, tx);
                cmd.Parameters.AddWithValue("c", connectionId);
                cmd.Parameters.Add(T("id", chunk.Select(k => (string?)k.Key)));
                cmd.Parameters.Add(T("upn", chunk.Select(k => (string?)k.Value.Upn)));
                cmd.Parameters.Add(T("nm", chunk.Select(k => k.Value.Name)));
                cmd.Parameters.Add(T("ml", chunk.Select(k => k.Value.Mail)));
                cmd.Parameters.Add(T("dp", chunk.Select(k => k.Value.Dept)));
                cmd.Parameters.Add(new NpgsqlParameter("en", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = chunk.Select(k => k.Value.Enabled).ToArray() });
                cmd.Parameters.Add(new NpgsqlParameter("si", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = chunk.Select(k => k.Value.SignIn?.ToUniversalTime()).ToArray() });
                cmd.Parameters.Add(T("sk", chunk.Select(k => (string?)string.Join(",", k.Value.Skus))));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Microsoft 365 as a web app: every user with a licence has an account.
            var m365 = await EnsureAppAsync(conn, tx, "microsoft-365", null, null, "m365", ct);
            var licensed = byId.Where(u => u.Value.Skus.Length > 0).Select(u => new AccountRow(u.Value.Upn, u.Value.Name, u.Value.Mail, "active",
                "user", u.Value.SignIn, string.Join(", ", u.Value.Skus.Select(s => skuNames.GetValueOrDefault(s, s)).Distinct()), u.Value.Dept, u.Key)).ToList();
            await ReplaceAccountsAsync(conn, tx, m365, "m365", licensed, ct);
            await EnsureProductsAsync(conn, tx, connectionId, m365, ct);
            await tx.CommitAsync(ct);
        }

        // ---- single sign-on applications and their users -----------------------------------------------------------
        int apps = 0, accounts = 0;
        if (syncApps)
        {
            List<JsonElement> sps;
            try
            {
                sps = await GetAllAsync(http, $"{graphBase}/v1.0/servicePrincipals?$filter=tags/any(t:t eq 'WindowsAzureActiveDirectoryIntegratedApp')" +
                                              "&$select=id,appId,displayName,homepage,loginUrl,replyUrls,appOwnerOrganizationId,accountEnabled,preferredSingleSignOnMode&$top=999", 2000, ct, "Application.Read.All");
            }
            catch (M365Exception ex) { notes.Add("Single sign-on applications not read: " + ex.Message); sps = []; }
            var groupCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in sps.Where(s => !MicrosoftTenants.Contains(Str(s, "appOwnerOrganizationId") ?? "")).Take(300))
            {
                var spId = Str(sp, "id"); var display = Str(sp, "displayName");
                if (spId is null || string.IsNullOrWhiteSpace(display)) continue;
                List<JsonElement> assigned;
                try { assigned = await GetAllAsync(http, $"{graphBase}/v1.0/servicePrincipals/{spId}/appRoleAssignedTo?$top=999", 20_000, ct, "Application.Read.All"); }
                catch (M365Exception) { continue; }
                var userIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in assigned)
                {
                    var pid = Str(a, "principalId"); if (pid is null) continue;
                    if (Str(a, "principalType") == "User") userIds.Add(pid);
                    else if (Str(a, "principalType") == "Group")
                    {
                        if (!groupCache.TryGetValue(pid, out var members))
                        {
                            try
                            {
                                members = (await GetAllAsync(http, $"{graphBase}/v1.0/groups/{pid}/transitiveMembers/microsoft.graph.user?$select=id&$top=999", 10_000, ct, "GroupMember.Read.All"))
                                    .Select(m => Str(m, "id")).Where(m => m is not null).Select(m => m!).ToList();
                            }
                            catch (M365Exception) { members = []; }
                            groupCache[pid] = members;
                        }
                        foreach (var m in members) userIds.Add(m);
                    }
                }
                // An app nobody is assigned to and that does not require assignment is still listed (no accounts).
                var url = Str(sp, "homepage") ?? Str(sp, "loginUrl") ??
                          (sp.TryGetProperty("replyUrls", out var ru) && ru.ValueKind == JsonValueKind.Array ? ru.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x is not null) : null);
                await using var tx = await conn.BeginTransactionAsync(ct);
                var appId = await EnsureEntraAppAsync(conn, tx, display!, Str(sp, "appId"), url, ct);
                var rows = userIds.Where(byId.ContainsKey).Select(id => byId[id]).Select(u => new AccountRow(u.Upn, u.Name, u.Mail, "active",
                    "user", u.SignIn, null, u.Dept, null)).ToList();
                await ReplaceAccountsAsync(conn, tx, appId, "entra", rows, ct);
                await tx.CommitAsync(ct);
                apps++; accounts += rows.Count;
            }
        }
        return new Outcome(skus.Count, byId.Count, apps, accounts, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    private sealed record AccountRow(string User, string? Name, string? Mail, string Status, string Role, DateTimeOffset? LastLogin, string? Tier, string? Department, string? ExternalId);

    /// <summary>The accounts of one source for an app become exactly these (others of that source are removed).</summary>
    private static async Task ReplaceAccountsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid appId, string source, List<AccountRow> rows, CancellationToken ct)
    {
        rows = rows.GroupBy(r => r.User.ToLowerInvariant()).Select(g => g.First()).ToList();
        foreach (var chunk in rows.Chunk(2000))
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO zentriq.webapp_accounts AS w (app_id, user_name, display_name, email, status, role, last_login, license_tier, department, external_id, source, synced_at)
                SELECT @a, x.u, x.n, x.m, x.s, x.r, x.l, x.t, x.d, x.e, @src, now()
                  FROM unnest(@u, @n, @m, @s, @r, @l, @t, @d, @e) AS x(u, n, m, s, r, l, t, d, e)
                ON CONFLICT (app_id, lower(user_name)) DO UPDATE SET display_name = COALESCE(EXCLUDED.display_name, w.display_name), email = COALESCE(EXCLUDED.email, w.email),
                    status = CASE WHEN w.source IN ('manual','import') AND w.status = 'removed' THEN w.status ELSE EXCLUDED.status END,
                    last_login = GREATEST(w.last_login, EXCLUDED.last_login), license_tier = COALESCE(EXCLUDED.license_tier, w.license_tier),
                    department = COALESCE(EXCLUDED.department, w.department), external_id = COALESCE(EXCLUDED.external_id, w.external_id),
                    source = CASE WHEN w.source IN ('manual','import','usage') THEN w.source ELSE EXCLUDED.source END, synced_at = now(), updated_at = now()
                """, conn, tx);
            cmd.Parameters.AddWithValue("a", appId);
            cmd.Parameters.AddWithValue("src", source);
            cmd.Parameters.Add(T("u", chunk.Select(r => (string?)r.User)));
            cmd.Parameters.Add(T("n", chunk.Select(r => r.Name)));
            cmd.Parameters.Add(T("m", chunk.Select(r => r.Mail)));
            cmd.Parameters.Add(T("s", chunk.Select(r => (string?)r.Status)));
            cmd.Parameters.Add(T("r", chunk.Select(r => (string?)r.Role)));
            cmd.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = chunk.Select(r => r.LastLogin?.ToUniversalTime()).ToArray() });
            cmd.Parameters.Add(T("t", chunk.Select(r => r.Tier)));
            cmd.Parameters.Add(T("d", chunk.Select(r => r.Department)));
            cmd.Parameters.Add(T("e", chunk.Select(r => r.ExternalId)));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using var del = new NpgsqlCommand("DELETE FROM zentriq.webapp_accounts WHERE app_id = @a AND source = @src AND NOT (lower(user_name) = ANY (@u))", conn, tx);
        del.Parameters.AddWithValue("a", appId);
        del.Parameters.AddWithValue("src", source);
        del.Parameters.AddWithValue("u", rows.Select(r => r.User.ToLowerInvariant()).ToArray());
        await del.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Guid> EnsureAppAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string catalogKey, string? entraAppId, string? url, string source, CancellationToken ct)
    {
        var e = WebAppCatalog.Standard.Find(catalogKey);
        await using var cmd = new NpgsqlCommand("""
            WITH found AS (SELECT id FROM zentriq.webapps WHERE catalog_key = @k OR lower(btrim(name)) = lower(@n) LIMIT 1),
            ins AS (INSERT INTO zentriq.webapps (catalog_key, name, vendor, category, url, domains, status, sso, source, entra_app_id, first_seen_at)
                    SELECT @k, @n, @v, @c, @u, @d, 'sanctioned', true, @src, @ea, now() WHERE NOT EXISTS (SELECT 1 FROM found) RETURNING id)
            SELECT id FROM found UNION ALL SELECT id FROM ins
            """, conn, tx);
        cmd.Parameters.AddWithValue("k", catalogKey);
        cmd.Parameters.AddWithValue("n", e?.Name ?? catalogKey);
        cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Text) { Value = (object?)e?.Vendor ?? DBNull.Value });
        cmd.Parameters.AddWithValue("c", e?.Category ?? "Other");
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = (object?)(url ?? e?.Url) ?? DBNull.Value });
        cmd.Parameters.AddWithValue("d", (e?.Domains ?? []).ToArray());
        cmd.Parameters.AddWithValue("src", source);
        cmd.Parameters.Add(new NpgsqlParameter("ea", NpgsqlDbType.Text) { Value = (object?)entraAppId ?? DBNull.Value });
        var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        await using var mark = new NpgsqlCommand("UPDATE zentriq.webapps SET sso = true, status = CASE WHEN status = 'review' THEN 'sanctioned' ELSE status END, updated_at = now() WHERE id = @id", conn, tx);
        mark.Parameters.AddWithValue("id", id);
        await mark.ExecuteNonQueryAsync(ct);
        return id;
    }

    /// <summary>A single sign-on application: the catalogue app its address belongs to, an app of that name, or a new app.</summary>
    private static async Task<Guid> EnsureEntraAppAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string displayName, string? entraAppId, string? url, CancellationToken ct)
    {
        var host = Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;
        var cat = WebAppCatalog.Standard.Apps.FirstOrDefault(a => host is not null && a.Domains.Any(d => WebAppCatalog.HostMatches(host, d)))
                  ?? WebAppCatalog.Standard.Apps.FirstOrDefault(a => a.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase));
        if (cat is not null) return await EnsureAppAsync(conn, tx, cat.Key, entraAppId, null, "entra", ct);

        await using var cmd = new NpgsqlCommand("""
            WITH found AS (SELECT id FROM zentriq.webapps WHERE (@ea::text IS NOT NULL AND entra_app_id = @ea) OR lower(btrim(name)) = lower(@n) LIMIT 1),
            ins AS (INSERT INTO zentriq.webapps (name, url, domains, status, sso, source, entra_app_id, first_seen_at)
                    SELECT @n, @u, @d, 'sanctioned', true, 'entra', @ea, now() WHERE NOT EXISTS (SELECT 1 FROM found) RETURNING id)
            SELECT id FROM found UNION ALL SELECT id FROM ins
            """, conn, tx);
        cmd.Parameters.AddWithValue("n", displayName.Length > 120 ? displayName[..120] : displayName);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = (object?)url ?? DBNull.Value });
        cmd.Parameters.AddWithValue("d", host is null || host.EndsWith("microsoftonline.com") ? Array.Empty<string>() : [host]);
        cmd.Parameters.Add(new NpgsqlParameter("ea", NpgsqlDbType.Text) { Value = (object?)entraAppId ?? DBNull.Value });
        var id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        await using var mark = new NpgsqlCommand("UPDATE zentriq.webapps SET sso = true, entra_app_id = COALESCE(entra_app_id, @ea), updated_at = now() WHERE id = @id", conn, tx);
        mark.Parameters.AddWithValue("id", id);
        mark.Parameters.Add(new NpgsqlParameter("ea", NpgsqlDbType.Text) { Value = (object?)entraAppId ?? DBNull.Value });
        await mark.ExecuteNonQueryAsync(ct);
        return id;
    }

    /// <summary>
    /// A software product and a subscription licence for every paid Microsoft 365 licence (SKU) that has none yet: its
    /// quantity and use come from Microsoft 365, its end date from the subscription's next renewal or expiry.
    /// </summary>
    private static async Task EnsureProductsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid connectionId, Guid m365App, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            WITH paid AS (
                SELECT k.* FROM zentriq.sam_cloud_skus k
                 WHERE k.connection_id = @c AND k.enabled_units + k.warning > 0 AND k.enabled_units < 10000 AND NOT (k.sku_part = ANY (@free))
                   AND k.sku_part !~* '(FREE|VIRAL|EXPLORATORY|_DEV$|TRIAL)'),
            newp AS (
                INSERT INTO zentriq.sam_products (name, publisher, category, metric, kind, needs_license, webapp_id, cloud_skus, catalog_key, created_by)
                SELECT DISTINCT ON (k.sku_part) COALESCE(k.name, k.sku_part), 'Microsoft', 'Cloud subscriptions', 'user', 'cloud', true, @app, ARRAY[k.sku_part], 'm365:' || k.sku_part, 'Microsoft 365 sync'
                  FROM paid k
                 WHERE NOT EXISTS (SELECT 1 FROM zentriq.sam_products p WHERE k.sku_part = ANY (p.cloud_skus) OR p.catalog_key = 'm365:' || k.sku_part)
                   AND NOT EXISTS (SELECT 1 FROM zentriq.sam_products p WHERE lower(btrim(p.name)) = lower(COALESCE(k.name, k.sku_part)))
                RETURNING id, cloud_skus[1] AS part)
            INSERT INTO zentriq.sam_licenses (product_id, name, license_type, metric, vendor, billing, end_date, cloud_connection_id, cloud_sku, synced, created_by, notify_days)
            SELECT p.id, COALESCE(k.name, k.sku_part) || ' (Microsoft 365)', CASE WHEN k.is_trial THEN 'trial' ELSE 'subscription' END, 'user', 'Microsoft', 'annual',
                   k.next_lifecycle::date, @c, k.sku_part, true, 'Microsoft 365 sync', 30
              FROM (SELECT id, part FROM newp
                    UNION SELECT p.id, k2.sku_part FROM zentriq.sam_products p JOIN paid k2 ON k2.sku_part = ANY (p.cloud_skus)) p
              JOIN paid k ON k.sku_part = p.part
             WHERE NOT EXISTS (SELECT 1 FROM zentriq.sam_licenses l WHERE l.cloud_connection_id = @c AND l.cloud_sku = k.sku_part);
            -- Licences kept by the sync follow the subscription's dates.
            UPDATE zentriq.sam_licenses l SET end_date = k.next_lifecycle::date, license_type = CASE WHEN k.is_trial THEN 'trial' ELSE 'subscription' END, updated_at = now()
              FROM zentriq.sam_cloud_skus k
             WHERE l.synced AND l.cloud_connection_id = @c AND k.connection_id = @c AND k.sku_part = l.cloud_sku
               AND (l.end_date IS DISTINCT FROM k.next_lifecycle::date OR l.license_type <> CASE WHEN k.is_trial THEN 'trial' ELSE 'subscription' END);
            """, conn, tx);
        cmd.Parameters.AddWithValue("c", connectionId);
        cmd.Parameters.AddWithValue("app", m365App);
        cmd.Parameters.AddWithValue("free", SkuNames.Free.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<string> TokenAsync(HttpClient http, string tenant, string client, string credential, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = client, ["client_secret"] = credential, ["scope"] = $"{graphBase}/.default",
        });
        HttpResponseMessage resp;
        try { resp = await http.PostAsync($"{loginBase}/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token", form, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new M365Exception($"Could not reach login.microsoftonline.com: {ex.Message}. The server needs outbound HTTPS to Microsoft.", ex); }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return JsonDocument.Parse(text).RootElement.GetProperty("access_token").GetString()!;
            string? desc = null;
            try { desc = JsonDocument.Parse(text).RootElement.GetProperty("error_description").GetString(); } catch (Exception) { /* not JSON */ }
            var first = desc?.Split('\n')[0].Trim() ?? $"HTTP {(int)resp.StatusCode}";
            throw new M365Exception(first switch
            {
                { } d when d.Contains("AADSTS7000215") => "Sign-in failed: the client secret is wrong. Copy the secret Value (not the Secret ID) from Certificates & secrets.",
                { } d when d.Contains("AADSTS7000222") => "Sign-in failed: the client secret has expired. Create a new one and update the connection.",
                { } d when d.Contains("AADSTS700016") => "Sign-in failed: no application with this client ID in the tenant.",
                { } d when d.Contains("AADSTS90002") || d.Contains("AADSTS900023") => "Sign-in failed: the tenant ID is wrong.",
                _ => "Sign-in failed: " + first,
            });
        }
    }

    /// <summary>GET with paging (@odata.nextLink) and retry on throttling. Throws <see cref="M365Exception"/> with the missing permission on 403.</summary>
    private static async Task<List<JsonElement>> GetAllAsync(HttpClient http, string url, int max, CancellationToken ct, string permission)
    {
        var list = new List<JsonElement>();
        string? next = url;
        while (next is not null && list.Count < max)
        {
            HttpResponseMessage resp = null!;
            for (var attempt = 1; ; attempt++)
            {
                try { resp = await http.GetAsync(next, ct); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                { throw new M365Exception($"Could not reach graph.microsoft.com: {ex.Message}", ex); }
                if ((resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) && attempt < 4)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                    resp.Dispose();
                    await Task.Delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, ct);
                    continue;
                }
                break;
            }
            using (resp)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    throw new M365Exception($"Access denied: grant the application permission {permission} (Microsoft Graph, application) and admin consent.");
                if (!resp.IsSuccessStatusCode)
                {
                    string? msg = null;
                    try { msg = JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("message").GetString(); } catch (Exception) { /* not JSON */ }
                    throw new M365Exception($"Microsoft Graph returned {(int)resp.StatusCode}: {msg ?? resp.ReasonPhrase}");
                }
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array)
                    foreach (var e in v.EnumerateArray()) list.Add(e.Clone());
                next = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
            }
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static NpgsqlParameter T(string name, IEnumerable<string?> values) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() };
}

/// <summary>Product names of common Microsoft 365 licences (SKU part numbers).</summary>
public static class SkuNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPE_E3"] = "Microsoft 365 E3", ["SPE_E5"] = "Microsoft 365 E5", ["SPE_F1"] = "Microsoft 365 F3", ["M365_F1"] = "Microsoft 365 F1",
        ["ENTERPRISEPACK"] = "Office 365 E3", ["ENTERPRISEPREMIUM"] = "Office 365 E5", ["STANDARDPACK"] = "Office 365 E1", ["DESKLESSPACK"] = "Office 365 F3",
        ["O365_BUSINESS_ESSENTIALS"] = "Microsoft 365 Business Basic", ["O365_BUSINESS_PREMIUM"] = "Microsoft 365 Business Standard",
        ["SPB"] = "Microsoft 365 Business Premium", ["O365_BUSINESS"] = "Microsoft 365 Apps for business", ["OFFICESUBSCRIPTION"] = "Microsoft 365 Apps for enterprise",
        ["EXCHANGESTANDARD"] = "Exchange Online (Plan 1)", ["EXCHANGEENTERPRISE"] = "Exchange Online (Plan 2)", ["EXCHANGEDESKLESS"] = "Exchange Online Kiosk",
        ["EMS"] = "Enterprise Mobility + Security E3", ["EMSPREMIUM"] = "Enterprise Mobility + Security E5",
        ["AAD_PREMIUM"] = "Microsoft Entra ID P1", ["AAD_PREMIUM_P2"] = "Microsoft Entra ID P2", ["INTUNE_A"] = "Microsoft Intune Plan 1",
        ["POWER_BI_PRO"] = "Power BI Pro", ["PBI_PREMIUM_PER_USER"] = "Power BI Premium Per User", ["POWER_BI_STANDARD"] = "Power BI (free)",
        ["PROJECTPROFESSIONAL"] = "Project Plan 3", ["PROJECTPREMIUM"] = "Project Plan 5", ["PROJECT_P1"] = "Project Plan 1",
        ["VISIOCLIENT"] = "Visio Plan 2", ["VISIOONLINE_PLAN1"] = "Visio Plan 1",
        ["Microsoft_365_Copilot"] = "Microsoft 365 Copilot", ["Microsoft_Teams_Premium"] = "Microsoft Teams Premium",
        ["MCOEV"] = "Microsoft Teams Phone Standard", ["MCOMEETADV"] = "Microsoft Teams Audio Conferencing", ["MCOPSTN1"] = "Microsoft Teams Domestic Calling Plan",
        ["Microsoft_Teams_Rooms_Pro"] = "Microsoft Teams Rooms Pro", ["Microsoft_Teams_Rooms_Basic"] = "Microsoft Teams Rooms Basic",
        ["ATP_ENTERPRISE"] = "Microsoft Defender for Office 365 (Plan 1)", ["THREAT_INTELLIGENCE"] = "Microsoft Defender for Office 365 (Plan 2)",
        ["WIN10_VDA_E3"] = "Windows Enterprise E3", ["WIN10_VDA_E5"] = "Windows Enterprise E5", ["WIN_DEF_ATP"] = "Microsoft Defender for Endpoint P2",
        ["DEFENDER_ENDPOINT_P1"] = "Microsoft Defender for Endpoint P1", ["RIGHTSMANAGEMENT"] = "Azure Information Protection Premium P1",
        ["FLOW_FREE"] = "Power Automate (free)", ["POWERAPPS_VIRAL"] = "Power Apps (trial)", ["TEAMS_EXPLORATORY"] = "Microsoft Teams Exploratory",
        ["STREAM"] = "Microsoft Stream", ["WINDOWS_STORE"] = "Windows Store for Business", ["POWERAPPS_DEV"] = "Power Apps Developer",
        ["DYN365_ENTERPRISE_SALES"] = "Dynamics 365 Sales Enterprise", ["DYN365_ENTERPRISE_CUSTOMER_SERVICE"] = "Dynamics 365 Customer Service Enterprise",
        ["POWERAPPS_PER_USER"] = "Power Apps Premium", ["FLOW_PER_USER"] = "Power Automate Premium", ["MCOSTANDARD"] = "Skype for Business Online (Plan 2)",
    };

    /// <summary>Free or self-service licences that are not tracked as purchases.</summary>
    public static readonly string[] Free =
        ["FLOW_FREE", "POWER_BI_STANDARD", "TEAMS_EXPLORATORY", "WINDOWS_STORE", "STREAM", "POWERAPPS_VIRAL", "POWERAPPS_DEV", "RMSBASIC", "MCOPSTNC",
         "CCIBOTS_PRIVPREV_VIRAL", "MICROSOFT_BUSINESS_CENTER", "Microsoft_Teams_Exploratory_Dept", "POWER_PAGES_VTRIAL_FOR_MAKERS", "Power_Pages_vTrial_for_Makers"];

    public static string Name(string part) => Names.TryGetValue(part, out var n) ? n : string.Join(" ", part.Split('_', StringSplitOptions.RemoveEmptyEntries));
}
