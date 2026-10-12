using Npgsql;
using NpgsqlTypes;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Infrastructure.ActiveDirectory;

public sealed record DirectoryConnectionInfo(Guid Id, string Name, DirectorySettings Settings, int InactiveDays);

/// <summary>Loads Active Directory connections (with the decrypted bind password) and stores synchronised objects.</summary>
public sealed class DirectoryStore(NpgsqlDataSource ds, CredentialProtector protector)
{
    public async Task<DirectoryConnectionInfo?> LoadAsync(Guid id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT name, server_host, dc_name, port, transport, verify_certificate, base_dn, username, secret_protected, inactive_days
              FROM zentriq.directory_connections WHERE id = @id
            """);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var password = S(8) is { } p ? protector.Unprotect(id, p).Password ?? "" : "";
        return new DirectoryConnectionInfo(id, r.GetString(0),
            new DirectorySettings(S(1) ?? "", S(2), r.GetInt32(3), r.GetString(4) == "ldaps", r.GetBoolean(5), S(6) ?? "", S(7) ?? "", password),
            r.GetInt32(9));
    }

    public async Task UpsertAsync(NpgsqlConnection conn, Guid connectionId, DirectoryEntryData e, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO zentriq.directory_objects (connection_id, object_guid, object_class, distinguished_name, sam_account_name, dns_host_name,
                operating_system, operating_system_version, enabled, last_logon_timestamp, name, display_name, mail, user_principal_name,
                department, title, description, parent_dn, member_of, when_created, password_last_set, synced_at)
            VALUES (@c, @g, @cls, @dn, @sam, @dns, @os, @osv, @en, @ll, @n, @dname, @mail, @upn, @dep, @title, @desc, @parent, @mo, @wc, @pls, now())
            ON CONFLICT (connection_id, object_guid) DO UPDATE SET
                object_class = EXCLUDED.object_class, distinguished_name = EXCLUDED.distinguished_name, sam_account_name = EXCLUDED.sam_account_name,
                dns_host_name = EXCLUDED.dns_host_name, operating_system = EXCLUDED.operating_system, operating_system_version = EXCLUDED.operating_system_version,
                enabled = EXCLUDED.enabled, last_logon_timestamp = EXCLUDED.last_logon_timestamp, name = EXCLUDED.name, display_name = EXCLUDED.display_name,
                mail = EXCLUDED.mail, user_principal_name = EXCLUDED.user_principal_name, department = EXCLUDED.department, title = EXCLUDED.title,
                description = EXCLUDED.description, parent_dn = EXCLUDED.parent_dn, member_of = EXCLUDED.member_of, when_created = EXCLUDED.when_created,
                password_last_set = EXCLUDED.password_last_set, synced_at = now()
            """, conn);
        NpgsqlParameter T(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = (object?)v ?? DBNull.Value };
        NpgsqlParameter D(string n, DateTimeOffset? v) => new(n, NpgsqlDbType.TimestampTz) { Value = (object?)v ?? DBNull.Value };
        cmd.Parameters.AddWithValue("c", connectionId);
        cmd.Parameters.AddWithValue("g", e.ObjectGuid);
        cmd.Parameters.AddWithValue("cls", e.ObjectClass);
        cmd.Parameters.AddWithValue("dn", e.DistinguishedName);
        cmd.Parameters.Add(T("sam", e.SamAccountName));
        cmd.Parameters.Add(T("dns", e.DnsHostName?.ToLowerInvariant()));
        cmd.Parameters.Add(T("os", e.OperatingSystem));
        cmd.Parameters.Add(T("osv", e.OperatingSystemVersion));
        cmd.Parameters.Add(new NpgsqlParameter("en", NpgsqlDbType.Boolean) { Value = (object?)e.Enabled ?? DBNull.Value });
        cmd.Parameters.Add(D("ll", e.LastLogon));
        cmd.Parameters.Add(T("n", e.Name));
        cmd.Parameters.Add(T("dname", e.DisplayName));
        cmd.Parameters.Add(T("mail", e.Mail));
        cmd.Parameters.Add(T("upn", e.UserPrincipalName));
        cmd.Parameters.Add(T("dep", e.Department));
        cmd.Parameters.Add(T("title", e.Title));
        cmd.Parameters.Add(T("desc", e.Description));
        cmd.Parameters.Add(T("parent", DirectoryClient.ParentDn(e.DistinguishedName)));
        cmd.Parameters.Add(new NpgsqlParameter("mo", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = e.MemberOf });
        cmd.Parameters.Add(D("wc", e.WhenCreated));
        cmd.Parameters.Add(D("pls", e.PasswordLastSet));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
