using Npgsql;
using NpgsqlTypes;
using Zentriq.Api.Infrastructure;
using Zentriq.Domain;
using Zentriq.Infrastructure.Auth;

namespace Zentriq.Api.Endpoints;

/// <summary>
/// Users, Roles &amp; Groups › Sign-in: console sign-in with Active Directory (LDAP / LDAPS) and Microsoft Entra ID (OpenID
/// Connect), and the directory groups that give Zentriq roles. Local accounts keep working.
/// </summary>
public static class SignInEndpoints
{
    public sealed record EntraBody(bool Enabled, string? TenantId, string? ClientId, bool? CreateUsers, string? ClientSecret);
    public sealed record TestBody(string? Username, string? Password);
    /// <summary>A directory group mapped to a Zentriq role (RoleId) or to a Zentriq group (GroupId).</summary>
    public sealed record MappingBody(string Provider, string GroupRef, string? GroupName, string? RoleId, Guid? GroupId = null);

    public static void MapSignInEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth/sign-in").WithTags("Directory sign-in").RequireAuthorization(Permissions.UsersManage);

        g.MapGet("", async (SignInStore store, NpgsqlDataSource ds, HttpContext http, CancellationToken ct) =>
        {
            var counts = new Dictionary<string, long>();
            await using (var cmd = ds.CreateCommand("SELECT auth_source, count(*) FROM zentriq.users GROUP BY auth_source"))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) counts[r.GetString(0)] = r.GetInt64(1);
            var entra = await store.EntraAsync(ct);
            return Results.Ok(new
            {
                ldap = await store.LdapAsync(ct),
                entra = new { entra.Enabled, entra.TenantId, entra.ClientId, entra.CreateUsers, secretStored = await store.EntraSecretStoredAsync(ct) },
                redirectUri = AuthEndpoints.RedirectUri(http),
                mappings = await store.MappingsAsync(ct),
                users = counts,
            });
        });

        g.MapPut("/ldap", async (LdapSignInSettings body, SignInStore store, RequestContext rc, CancellationToken ct) =>
        {
            body.Host = (body.Host ?? "").Trim();
            if (body.Enabled && body.Host.Length == 0) return Validation.Problem("host", "Enter the domain controller or the domain's DNS name.");
            if (body.Host.Length > 253 || (body.Host.Length > 0 && Uri.CheckHostName(body.Host) == UriHostNameType.Unknown)) return Validation.Problem("host", "Enter a host name or IP address.");
            if (body.Port is < 1 or > 65535) return Validation.Problem("port", "Enter a port (636 for LDAPS, 389 for LDAP).");
            body.BaseDn = string.IsNullOrWhiteSpace(body.BaseDn) ? null : body.BaseDn.Trim();
            body.Domain = string.IsNullOrWhiteSpace(body.Domain) ? null : body.Domain.Trim();
            body.DcName = string.IsNullOrWhiteSpace(body.DcName) ? null : body.DcName.Trim();
            await store.SaveAsync("ldap", body, null, rc.UserName, ct);
            await rc.AuditAsync("auth.signin.ldap", body.Host, detail: new { body.Enabled, body.Host, body.Port, body.Ldaps, body.VerifyCertificate, body.BaseDn, body.Domain, body.CreateUsers });
            return Results.NoContent();
        });

        g.MapPut("/entra", async (EntraBody body, SignInStore store, RequestContext rc, CancellationToken ct) =>
        {
            var tenant = (body.TenantId ?? "").Trim();
            var client = (body.ClientId ?? "").Trim();
            if ((body.Enabled || tenant.Length > 0) && !EntraOidc.ValidTenant(tenant)) return Validation.Problem("tenantId", "Enter the Directory (tenant) ID — a GUID shown on the app registration's Overview page.");
            if ((body.Enabled || client.Length > 0) && !Guid.TryParse(client, out _)) return Validation.Problem("clientId", "Enter the Application (client) ID — a GUID.");
            if (body.ClientSecret is { Length: > 500 }) return Validation.Problem("clientSecret", "The client secret is too long.");
            if (body.Enabled && string.IsNullOrEmpty(body.ClientSecret) && !await store.EntraSecretStoredAsync(ct)) return Validation.Problem("clientSecret", "Enter the client secret value.");
            var cfg = new EntraSignInSettings { Enabled = body.Enabled, TenantId = tenant, ClientId = client, CreateUsers = body.CreateUsers ?? true };
            await store.SaveAsync("entra", cfg, string.IsNullOrWhiteSpace(body.ClientSecret) ? null : body.ClientSecret.Trim(), rc.UserName, ct);
            await rc.AuditAsync("auth.signin.entra", tenant, detail: new { body.Enabled, tenant, client, cfg.CreateUsers, secretChanged = !string.IsNullOrWhiteSpace(body.ClientSecret) });
            return Results.NoContent();
        });

        // Signs in with a directory account (nothing is created or changed) and shows the groups found and the roles they give.
        g.MapPost("/ldap/test", async (TestBody body, SignInStore store, RequestContext rc, CancellationToken ct) =>
        {
            var ldap = await store.LdapAsync(ct);
            DirectoryIdentity who;
            try { who = await Task.Run(() => LdapSignIn.Authenticate(ldap, body.Username ?? "", body.Password ?? ""), ct); }
            catch (DirectorySignInException ex)
            {
                await rc.AuditAsync("auth.signin.test", body.Username, detail: new { ok = false, ex.Reason });
                return Results.Ok(new { ok = false, message = ex.Message });
            }
            var mappings = (await store.MappingsAsync(ct)).Where(m => m.Provider == "ldap").ToList();
            var refs = who.Groups.Select(x => x.Ref.ToLowerInvariant()).ToHashSet();
            var hits = mappings.Where(m => refs.Contains(m.GroupRef.ToLowerInvariant())).ToList();
            var roles = hits.Where(m => m.RoleId is not null).Select(m => m.RoleId!).Concat(hits.Where(m => m.ZentriqGroup is not null).Select(m => "group " + m.ZentriqGroup)).Distinct().ToList();
            await rc.AuditAsync("auth.signin.test", body.Username, detail: new { ok = true, groups = who.Groups.Count, roles });
            return Results.Ok(new
            {
                ok = true,
                message = roles.Count > 0 ? $"Signed in as {who.DisplayName} ({who.Email}). Roles: {string.Join(", ", roles)}." : $"Signed in as {who.DisplayName} ({who.Email}), but none of the {who.Groups.Count} group(s) is mapped to a role yet.",
                email = who.Email, displayName = who.DisplayName, groups = who.Groups.Select(x => new { dn = x.Ref, name = x.Name }), roles,
            });
        });

        g.MapPost("/mappings", async (MappingBody body, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (rc.Scoped) return RequestContext.ScopedRefused();
            if ((body.RoleId is { Length: > 0 }) == (body.GroupId is not null)) return Validation.Problem("roleId", "Choose a role or a Zentriq group.");
            if (body.Provider is not ("ldap" or "entra")) return Validation.Problem("provider", "Choose Active Directory or Microsoft Entra ID.");
            var gref = (body.GroupRef ?? "").Trim();
            if (gref.Length is 0 or > 1000) return Validation.Problem("groupRef", body.Provider == "ldap" ? "Enter the group's distinguished name (CN=…,OU=…,DC=…)." : "Enter the group's object ID or an app role value.");
            if (body.Provider == "ldap" && !gref.Contains('=')) return Validation.Problem("groupRef", "Enter the group's distinguished name, for example CN=Zentriq Admins,OU=Groups,DC=contoso,DC=com.");
            await using var cmd = ds.CreateCommand(body.GroupId is null ? """
                INSERT INTO zentriq.auth_group_mappings (provider, group_ref, group_name, role_id, created_by)
                SELECT @p, @g, @n, r.id, @by FROM zentriq.roles r WHERE r.id = @r
                ON CONFLICT (provider, lower(group_ref), role_id) DO UPDATE SET group_name = COALESCE(EXCLUDED.group_name, zentriq.auth_group_mappings.group_name)
                RETURNING id
                """ : """
                INSERT INTO zentriq.auth_group_mappings (provider, group_ref, group_name, group_id, created_by)
                SELECT @p, @g, @n, ug.id, @by FROM zentriq.user_groups ug WHERE ug.id = @gid
                ON CONFLICT (provider, lower(group_ref), group_id) WHERE group_id IS NOT NULL DO UPDATE SET group_name = COALESCE(EXCLUDED.group_name, zentriq.auth_group_mappings.group_name)
                RETURNING id
                """);
            cmd.Parameters.AddWithValue("p", body.Provider);
            cmd.Parameters.AddWithValue("g", gref);
            cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(body.GroupName) ? DBNull.Value : body.GroupName.Trim() });
            cmd.Parameters.AddWithValue("r", body.RoleId ?? "");
            cmd.Parameters.AddWithValue("gid", body.GroupId ?? Guid.Empty);
            cmd.Parameters.AddWithValue("by", rc.UserName);
            if (await cmd.ExecuteScalarAsync(ct) is not Guid id) return Validation.Problem("roleId", body.GroupId is null ? "Choose a role." : "Choose a Zentriq group.");
            await rc.AuditAsync("auth.signin.mapping", gref, detail: new { body.Provider, gref, body.GroupName, body.RoleId, body.GroupId });
            return Results.Ok(new { id });
        });

        g.MapDelete("/mappings/{id:guid}", async (Guid id, NpgsqlDataSource ds, RequestContext rc, CancellationToken ct) =>
        {
            if (rc.Scoped) return RequestContext.ScopedRefused();
            string provider, gref, role; Guid? group;
            await using (var cmd = ds.CreateCommand("DELETE FROM zentriq.auth_group_mappings WHERE id = @id RETURNING provider, group_ref, COALESCE(role_id, ''), group_id"))
            {
                cmd.Parameters.AddWithValue("id", id);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return Results.NotFound();
                provider = r.GetString(0); gref = r.GetString(1); role = r.GetString(2); group = r.IsDBNull(3) ? null : r.GetGuid(3);
            }
            if (group is { } gid)
            {
                await using var revokeGroup = ds.CreateCommand("""
                    WITH d AS (DELETE FROM zentriq.user_directory_groups WHERE provider = @p AND group_id = @g RETURNING user_id)
                    UPDATE zentriq.users SET access_stamp = gen_random_uuid() WHERE id IN (SELECT user_id FROM d)
                    """);
                revokeGroup.Parameters.AddWithValue("p", provider);
                revokeGroup.Parameters.AddWithValue("g", gid);
                await revokeGroup.ExecuteNonQueryAsync(ct);
                await rc.AuditAsync("auth.signin.mapping_removed", gref, detail: new { provider, group = gid });
                return Results.NoContent();
            }
            // The role is taken away from the directory users who had it at once (those in another group mapped to it get
            // it back at their next sign-in); their sessions reload their permissions.
            await using (var revoke = ds.CreateCommand("""
                WITH d AS (DELETE FROM zentriq.user_directory_roles WHERE provider = @p AND role_id = @r RETURNING user_id)
                UPDATE zentriq.users SET access_stamp = gen_random_uuid() WHERE id IN (SELECT user_id FROM d)
                """))
            {
                revoke.Parameters.AddWithValue("p", provider);
                revoke.Parameters.AddWithValue("r", role);
                await revoke.ExecuteNonQueryAsync(ct);
            }
            await rc.AuditAsync("auth.signin.mapping_removed", gref, detail: new { provider, role });
            return Results.NoContent();
        });
    }
}
