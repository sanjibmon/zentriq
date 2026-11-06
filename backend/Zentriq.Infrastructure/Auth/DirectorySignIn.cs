using System.DirectoryServices.Protocols;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.ActiveDirectory;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.Auth;

/// <summary>Console sign-in with Active Directory accounts (LDAP / LDAPS bind with the user's own password).</summary>
public sealed class LdapSignInSettings
{
    public bool Enabled { get; set; }
    /// <summary>Domain controller (or the domain's DNS name).</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 636;
    public bool Ldaps { get; set; } = true;
    public bool VerifyCertificate { get; set; } = true;
    /// <summary>The DC's name when <see cref="Host"/> is an IP address (certificate name check).</summary>
    public string? DcName { get; set; }
    /// <summary>Where users and groups are searched; empty: the whole domain.</summary>
    public string? BaseDn { get; set; }
    /// <summary>Domain added to plain user names ("CONTOSO" or "contoso.com").</summary>
    public string? Domain { get; set; }
    /// <summary>Create the Zentriq account at the first sign-in of a user in a mapped group.</summary>
    public bool CreateUsers { get; set; } = true;
}

/// <summary>Console sign-in with Microsoft Entra ID (OpenID Connect, authorization code with PKCE).</summary>
public sealed class EntraSignInSettings
{
    public bool Enabled { get; set; }
    /// <summary>Directory (tenant) ID.</summary>
    public string TenantId { get; set; } = "";
    /// <summary>Application (client) ID of the app registration.</summary>
    public string ClientId { get; set; } = "";
    public bool CreateUsers { get; set; } = true;
}

/// <summary>A directory group (LDAP distinguished name, Entra group object ID or app role value) that gives a Zentriq role.</summary>
public sealed record GroupMapping(Guid Id, string Provider, string GroupRef, string? GroupName, string RoleId);

/// <summary>A person signed in by the directory: a stable id, e-mail, name and the groups they are in.</summary>
public sealed record DirectoryIdentity(string Provider, string ExternalId, string Email, string DisplayName, IReadOnlyList<(string Ref, string? Name)> Groups);

public sealed class DirectorySignInException(string message, string reason) : Exception(message)
{
    /// <summary>Short reason for the audit log (wrong-password, no-group, …).</summary>
    public string Reason { get; } = reason;
}

/// <summary>Storage of the sign-in settings and group mappings (auth_providers, auth_group_mappings).</summary>
public sealed class SignInStore(NpgsqlDataSource ds, CredentialProtector protector)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly Guid EntraSecretId = new("5f0c2a8e-7d1b-4c39-9e55-2b6f4d8a1c01");

    public async Task<LdapSignInSettings> LdapAsync(CancellationToken ct) => await ConfigAsync<LdapSignInSettings>("ldap", ct);
    public async Task<EntraSignInSettings> EntraAsync(CancellationToken ct) => await ConfigAsync<EntraSignInSettings>("entra", ct);

    private async Task<T> ConfigAsync<T>(string id, CancellationToken ct) where T : new()
    {
        await using var cmd = ds.CreateCommand("SELECT config::text FROM zentriq.auth_providers WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) is string s ? JsonSerializer.Deserialize<T>(s, Json) ?? new T() : new T();
    }

    public async Task<bool> EntraSecretStoredAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT secret_protected IS NOT NULL FROM zentriq.auth_providers WHERE id = 'entra'");
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<string?> EntraSecretAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT secret_protected FROM zentriq.auth_providers WHERE id = 'entra'");
        return await cmd.ExecuteScalarAsync(ct) is string p ? protector.Unprotect(EntraSecretId, p).Password : null;
    }

    /// <summary>Saves a provider's settings; <paramref name="secret"/> null keeps the stored client secret.</summary>
    public async Task SaveAsync(string id, object config, string? secret, string? by, CancellationToken ct)
    {
        var prot = secret is { Length: > 0 } ? protector.Protect(EntraSecretId, new CredentialSecret { Password = secret }) : null;
        await using var cmd = ds.CreateCommand("""
            INSERT INTO zentriq.auth_providers AS p (id, enabled, config, secret_protected, updated_at, updated_by)
            VALUES (@id, @en, @c::jsonb, @s, now(), @by)
            ON CONFLICT (id) DO UPDATE SET enabled = EXCLUDED.enabled, config = EXCLUDED.config,
                   secret_protected = COALESCE(EXCLUDED.secret_protected, p.secret_protected), updated_at = now(), updated_by = EXCLUDED.updated_by
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("en", config is LdapSignInSettings l ? l.Enabled : config is EntraSignInSettings e && e.Enabled);
        cmd.Parameters.AddWithValue("c", JsonSerializer.Serialize(config, config.GetType(), Json));
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = (object?)prot ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("by", NpgsqlDbType.Text) { Value = (object?)by ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<GroupMapping>> MappingsAsync(CancellationToken ct)
    {
        var list = new List<GroupMapping>();
        await using var cmd = ds.CreateCommand("SELECT id, provider, group_ref, group_name, role_id FROM zentriq.auth_group_mappings ORDER BY provider, lower(COALESCE(group_name, group_ref)), role_id");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new GroupMapping(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4)));
        return list;
    }
}

/// <summary>Signs a person in against Active Directory with their own password and reads the groups they are in (nested too).</summary>
public static class LdapSignIn
{
    /// <summary>Escapes a value for an LDAP search filter (RFC 4515).</summary>
    public static string Escape(string v)
    {
        var sb = new StringBuilder();
        foreach (var c in v)
            sb.Append(c switch { '\\' => "\\5c", '*' => "\\2a", '(' => "\\28", ')' => "\\29", '\0' => "\\00", _ => c.ToString() });
        return sb.ToString();
    }

    /// <summary>The bind name and the account name to search for: DOMAIN\user, user@domain or a plain name (the configured domain is added).</summary>
    public static (string Bind, string Sam, string? Upn) Names(string input, string? domain)
    {
        var u = input.Trim();
        var slash = u.IndexOf('\\');
        if (slash > 0) return (u, u[(slash + 1)..], null);
        if (u.Contains('@')) return (u, u[..u.IndexOf('@')], u);
        return (string.IsNullOrWhiteSpace(domain) ? u : $"{domain.Trim()}\\{u}", u, null);
    }

    public static DirectoryIdentity Authenticate(LdapSignInSettings s, string username, string password)
    {
        // An empty password would be an anonymous ("unauthenticated") bind, which many servers accept.
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(username)) throw new DirectorySignInException("Enter your user name and password.", "empty");
        if (username.TrimStart().StartsWith("CN=", StringComparison.OrdinalIgnoreCase) || username.Length > 256) throw new DirectorySignInException("Enter DOMAIN\\user or user@domain.", "bad-name");
        if (string.IsNullOrWhiteSpace(s.Host)) throw new DirectorySignInException("Active Directory sign-in is not set up.", "not-configured");
        var (bind, sam, upn) = Names(username, s.Domain);
        LdapConnection conn;
        try { conn = DirectoryClient.Connect(new DirectorySettings(s.Host, s.DcName, s.Port, s.Ldaps, s.VerifyCertificate, s.BaseDn ?? "", bind, password), TimeSpan.FromSeconds(15)); }
        catch (DirectoryException ex) when (ex.Message.Contains("LDAP 49", StringComparison.Ordinal))
        {
            throw new DirectorySignInException("The user name or password is incorrect.", "bad-password");
        }
        catch (DirectoryException ex) { throw new DirectorySignInException(ex.Message, "directory-unavailable"); }
        catch (LdapException ex) { throw new DirectorySignInException("The domain controller cannot be reached: " + ex.Message, "directory-unavailable"); }
        using (conn)
        {
            var baseDn = s.BaseDn;
            if (string.IsNullOrWhiteSpace(baseDn))
            {
                var root = (SearchResponse)conn.SendRequest(new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "defaultNamingContext"));
                baseDn = root.Entries.Count > 0 && root.Entries[0].Attributes.Contains("defaultNamingContext") ? root.Entries[0].Attributes["defaultNamingContext"][0]?.ToString() : null;
                if (string.IsNullOrEmpty(baseDn)) throw new DirectorySignInException("The domain's naming context could not be read; enter the base DN.", "directory-unavailable");
            }
            var filter = upn is null
                ? $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={Escape(sam)}))"
                : $"(&(objectCategory=person)(objectClass=user)(|(userPrincipalName={Escape(upn)})(sAMAccountName={Escape(sam)})))";
            var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, "objectGUID", "distinguishedName", "mail", "userPrincipalName", "displayName", "sAMAccountName", "userAccountControl") { SizeLimit = 2 };
            SearchResponse resp;
            try { resp = (SearchResponse)conn.SendRequest(req); }
            catch (DirectoryOperationException ex) { throw new DirectorySignInException("Your account could not be read in the directory: " + ex.Message, "lookup-failed"); }
            if (resp.Entries.Count != 1) throw new DirectorySignInException($"Signed in, but the account was not found under {baseDn}.", "not-under-base");
            var e = resp.Entries[0];
            string? A(string n) => e.Attributes.Contains(n) && e.Attributes[n].Count > 0 ? e.Attributes[n][0] as string ?? Encoding.UTF8.GetString((byte[])e.Attributes[n][0]) : null;
            if (int.TryParse(A("userAccountControl"), out var uac) && (uac & 2) != 0) throw new DirectorySignInException("The account is disabled in Active Directory.", "disabled");
            var guidBytes = e.Attributes.Contains("objectGUID") ? e.Attributes["objectGUID"].GetValues(typeof(byte[])).OfType<byte[]>().FirstOrDefault() : null;
            var id = guidBytes is { Length: 16 } ? new Guid(guidBytes).ToString() : e.DistinguishedName.ToLowerInvariant();
            var dn = e.DistinguishedName;
            var groups = new List<(string, string?)>();
            var greq = new SearchRequest(baseDn, $"(&(objectClass=group)(member:1.2.840.113556.1.4.1941:={Escape(dn)}))", SearchScope.Subtree, "distinguishedName", "name");
            var page = new PageResultRequestControl(500);
            greq.Controls.Add(page);
            for (var i = 0; i < 20; i++)
            {
                SearchResponse g;
                try { g = (SearchResponse)conn.SendRequest(greq); }
                catch (DirectoryOperationException) { break; }
                foreach (SearchResultEntry ge in g.Entries)
                    groups.Add((ge.DistinguishedName, ge.Attributes.Contains("name") ? ge.Attributes["name"][0]?.ToString() : null));
                var pr = g.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
                if (pr is null || pr.Cookie.Length == 0) break;
                page.Cookie = pr.Cookie;
            }
            var samName = A("sAMAccountName") ?? sam;
            var email = A("mail") ?? A("userPrincipalName") ?? (string.IsNullOrWhiteSpace(s.Domain) || !s.Domain.Contains('.') ? samName : $"{samName}@{s.Domain}");
            return new DirectoryIdentity("ldap", id, email.Trim(), A("displayName") ?? samName, groups);
        }
    }
}

/// <summary>
/// The Zentriq account of a person signed in by the directory: found by the directory's id, linked once to an account
/// an administrator created with the same e-mail and directory sign-in, or created at the first sign-in. The roles of
/// the mapped directory groups are given at every sign-in (and removed when the person left the group).
/// </summary>
public static class DirectoryUsers
{
    public sealed record Outcome(Guid UserId, IReadOnlyList<string> Roles, bool Created);

    public static async Task<Outcome> ResolveAsync(NpgsqlDataSource ds, DirectoryIdentity who, bool createUsers, CancellationToken ct)
    {
        var refs = who.Groups.Select(g => g.Ref.ToLowerInvariant()).Distinct().ToArray();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var roles = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT DISTINCT role_id FROM zentriq.auth_group_mappings WHERE provider = @p AND lower(group_ref) = ANY(@g) ORDER BY 1", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", who.Provider);
            cmd.Parameters.AddWithValue("g", refs);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) roles.Add(r.GetString(0));
        }

        Guid? userId = null; string? status = null;
        await using (var cmd = new NpgsqlCommand("SELECT id, status FROM zentriq.users WHERE auth_source = @p AND external_id = @x", conn, tx))
        {
            cmd.Parameters.AddWithValue("p", who.Provider);
            cmd.Parameters.AddWithValue("x", who.ExternalId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { userId = r.GetGuid(0); status = r.GetString(1); }
        }
        if (userId is null)
        {
            await using var cmd = new NpgsqlCommand("SELECT id, status, auth_source, external_id FROM zentriq.users WHERE lower(email) = lower(@e)", conn, tx);
            cmd.Parameters.AddWithValue("e", who.Email);
            Guid? byEmail = null; string? source = null, ext = null;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct)) { byEmail = r.GetGuid(0); status = r.GetString(1); source = r.GetString(2); ext = r.IsDBNull(3) ? null : r.GetString(3); }
            if (byEmail is not null)
            {
                if (source != who.Provider)
                    throw new DirectorySignInException($"A Zentriq account with a different sign-in method already uses {who.Email}. Sign in with that account, or ask an administrator to change it to directory sign-in.", "email-in-use");
                if (ext is not null) throw new DirectorySignInException($"Another directory account is already linked to {who.Email}.", "email-in-use");
                await using var link = new NpgsqlCommand("UPDATE zentriq.users SET external_id = @x, updated_at = now() WHERE id = @id", conn, tx);
                link.Parameters.AddWithValue("x", who.ExternalId);
                link.Parameters.AddWithValue("id", byEmail.Value);
                await link.ExecuteNonQueryAsync(ct);
                userId = byEmail;
            }
        }
        var created = false;
        if (userId is null)
        {
            if (!createUsers) throw new DirectorySignInException("There is no Zentriq account for you yet. Ask a Zentriq administrator to add you.", "no-account");
            if (roles.Count == 0) throw new DirectorySignInException("You are not in a directory group that has access to Zentriq.", "no-group");
            await using var ins = new NpgsqlCommand("""
                INSERT INTO zentriq.users (email, display_name, password_hash, auth_source, external_id) VALUES (@e, @n, 'AA==', @p, @x) RETURNING id
                """, conn, tx);
            ins.Parameters.AddWithValue("e", who.Email);
            ins.Parameters.AddWithValue("n", Clip(who.DisplayName, 200));
            ins.Parameters.AddWithValue("p", who.Provider);
            ins.Parameters.AddWithValue("x", who.ExternalId);
            userId = (Guid)(await ins.ExecuteScalarAsync(ct))!;
            status = "active";
            created = true;
        }
        if (status != "active") throw new DirectorySignInException("Your Zentriq account is disabled.", "disabled");

        // Directory roles: exactly the mapped groups' roles.
        int changed;
        await using (var del = new NpgsqlCommand("DELETE FROM zentriq.user_directory_roles WHERE user_id = @u AND NOT (role_id = ANY(@r))", conn, tx))
        {
            del.Parameters.AddWithValue("u", userId.Value);
            del.Parameters.AddWithValue("r", roles.ToArray());
            changed = await del.ExecuteNonQueryAsync(ct);
        }
        await using (var add = new NpgsqlCommand("""
            INSERT INTO zentriq.user_directory_roles (user_id, role_id, provider) SELECT @u, x.role_id, @p FROM unnest(@r) AS x(role_id)
            ON CONFLICT DO NOTHING
            """, conn, tx))
        {
            add.Parameters.AddWithValue("u", userId.Value);
            add.Parameters.AddWithValue("r", roles.ToArray());
            add.Parameters.AddWithValue("p", who.Provider);
            changed += await add.ExecuteNonQueryAsync(ct);
        }
        await using (var upd = new NpgsqlCommand("""
            UPDATE zentriq.users SET display_name = CASE WHEN @n <> '' THEN @n ELSE display_name END, updated_at = now(),
                   access_stamp = CASE WHEN @changed THEN gen_random_uuid() ELSE access_stamp END
             WHERE id = @u
            """, conn, tx))
        {
            upd.Parameters.AddWithValue("u", userId.Value);
            upd.Parameters.AddWithValue("n", Clip(who.DisplayName, 200));
            upd.Parameters.AddWithValue("changed", changed > 0);
            await upd.ExecuteNonQueryAsync(ct);
        }
        long total;
        await using (var cnt = new NpgsqlCommand("SELECT count(*) FROM zentriq.user_role_grants WHERE user_id = @u", conn, tx))
        {
            cnt.Parameters.AddWithValue("u", userId.Value);
            total = (long)(await cnt.ExecuteScalarAsync(ct))!;
        }
        // Kept even when the sign-in is refused below: a person who left every mapped group loses the roles at once.
        await tx.CommitAsync(ct);
        if (total == 0) throw new DirectorySignInException("You are not in a directory group that has access to Zentriq.", "no-group");
        return new Outcome(userId.Value, roles, created);
    }

    private static string Clip(string s, int max) => s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
}
