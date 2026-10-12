using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Zentriq.Infrastructure.ActiveDirectory;

/// <summary>Everything needed to authenticate to a domain controller.</summary>
public sealed record DirectorySettings(
    string ServerHost, string? DcName, int Port, bool Ldaps, bool VerifyCertificate, string BaseDn, string Username, string Password);

public sealed record DirectoryEntryData(
    Guid ObjectGuid, string ObjectClass, string DistinguishedName, string? Name, string? SamAccountName, string? DisplayName,
    string? Mail, string? UserPrincipalName, string? DnsHostName, string? OperatingSystem, string? OperatingSystemVersion,
    bool? Enabled, DateTimeOffset? LastLogon, DateTimeOffset? WhenCreated, DateTimeOffset? PasswordLastSet,
    string? Department, string? Title, string? Description, string[] MemberOf);

public sealed record DirectoryTestResult(bool Ok, string Message, string? DefaultNamingContext, string? DnsHostName, int? SampleCount);

/// <summary>
/// Read-only Active Directory access over LDAPS (636) or LDAP (389, signed and sealed with Kerberos/NTLM).
/// Accepts DOMAIN\user, user@domain or a distinguished name (DN binds require LDAPS). Paged searches only.
/// </summary>
public static class DirectoryClient
{
    public const string Computers = "(objectCategory=computer)";
    public const string Users = "(&(objectCategory=person)(objectClass=user))";
    public const string Groups = "(objectClass=group)";
    public const string OrganizationalUnits = "(objectClass=organizationalUnit)";

    private static readonly string[] Attributes =
    [
        "objectGUID", "objectClass", "distinguishedName", "name", "sAMAccountName", "displayName", "mail", "userPrincipalName",
        "dNSHostName", "operatingSystem", "operatingSystemVersion", "userAccountControl", "lastLogonTimestamp", "whenCreated",
        "pwdLastSet", "department", "title", "description", "memberOf",
    ];

    public static LdapConnection Connect(DirectorySettings s, TimeSpan timeout)
    {
        var host = s.ServerHost.Trim();
        var isDn = s.Username.TrimStart().StartsWith("CN=", StringComparison.OrdinalIgnoreCase);
        if (isDn && !s.Ldaps) throw new DirectoryException("A distinguished-name bind sends the password in a simple bind; use LDAPS (port 636) or a DOMAIN\\user / user@domain account.");
        NetworkCredential cred;
        var u = s.Username.Trim();
        var slash = u.IndexOf('\\');
        if (isDn) cred = new NetworkCredential(u, s.Password);
        else if (slash > 0) cred = new NetworkCredential(u[(slash + 1)..], s.Password, u[..slash]);
        else cred = new NetworkCredential(u, s.Password);

        var conn = new LdapConnection(new LdapDirectoryIdentifier(host, s.Port), cred, isDn ? AuthType.Basic : AuthType.Negotiate) { Timeout = timeout };
        conn.SessionOptions.ProtocolVersion = 3;
        conn.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (s.Ldaps)
        {
            conn.SessionOptions.SecureSocketLayer = true;
            var expected = string.IsNullOrWhiteSpace(s.DcName) ? null : s.DcName.Trim();
            conn.SessionOptions.VerifyServerCertificate = (_, cert) =>
            {
                if (!s.VerifyCertificate) return true;
                using var c2 = new X509Certificate2(cert);
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                if (!chain.Build(c2)) return false;
                // When connecting by IP address the name check is done against the DC name given in the connection.
                return expected is null || c2.MatchesHostname(expected, allowWildcards: true);
            };
        }
        else
        {
            conn.SessionOptions.Signing = true;
            conn.SessionOptions.Sealing = true;
        }
        try { conn.Bind(); }
        catch (LdapException ex)
        {
            conn.Dispose();
            throw new DirectoryException(Explain(ex, s));
        }
        return conn;
    }

    public static string Explain(LdapException ex, DirectorySettings s) => ex.ErrorCode switch
    {
        49 => "The domain controller rejected the user name or password (LDAP 49 invalid credentials).",
        81 => $"Cannot reach the domain controller at {s.ServerHost}:{s.Port} (server down, wrong address, firewall, or {(s.Ldaps ? "no LDAPS certificate on the DC / certificate not trusted by this server" : "LDAP blocked")}).",
        85 => "The domain controller did not answer in time.",
        8 => "The domain controller requires a stronger authentication or LDAP signing.",
        _ => $"LDAP error {ex.ErrorCode}: {ex.Message}",
    };

    public static DirectoryTestResult Test(DirectorySettings s, TimeSpan timeout)
    {
        try
        {
            using var conn = Connect(s, timeout);
            var root = (SearchResponse)conn.SendRequest(new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "defaultNamingContext", "dnsHostName"));
            var e = root.Entries.Count > 0 ? root.Entries[0] : null;
            string? A(string n) => e is not null && e.Attributes.Contains(n) && e.Attributes[n].Count > 0 ? e.Attributes[n][0]?.ToString() : null;
            if (string.IsNullOrWhiteSpace(s.BaseDn))
                return new DirectoryTestResult(true, $"Signed in to {A("dnsHostName") ?? s.ServerHost} as {s.Username}.", A("defaultNamingContext"), A("dnsHostName"), null);
            var probe = new SearchRequest(s.BaseDn, Computers, SearchScope.Subtree, "objectGUID") { SizeLimit = 5 };
            int count;
            try { count = ((SearchResponse)conn.SendRequest(probe)).Entries.Count; }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded) { count = 5; }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
            {
                return new DirectoryTestResult(false, $"Signed in, but '{s.BaseDn}' does not exist in this domain (default naming context: {A("defaultNamingContext")}).", A("defaultNamingContext"), A("dnsHostName"), null);
            }
            return new DirectoryTestResult(true, $"Connected to {A("dnsHostName") ?? s.ServerHost} and read '{s.BaseDn}'.", A("defaultNamingContext"), A("dnsHostName"), count);
        }
        catch (DirectoryException ex) { return new DirectoryTestResult(false, ex.Message, null, null, null); }
        catch (LdapException ex) { return new DirectoryTestResult(false, Explain(ex, s), null, null, null); }
    }

    /// <summary>Paged search returning normalized entries.</summary>
    public static IEnumerable<DirectoryEntryData> Search(LdapConnection conn, string baseDn, string filter, string objectClass, int limit)
    {
        var paging = new PageResultRequestControl(500);
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, Attributes);
        request.Controls.Add(paging);
        var n = 0;
        while (true)
        {
            var response = (SearchResponse)conn.SendRequest(request);
            foreach (SearchResultEntry e in response.Entries)
            {
                var item = Map(e, objectClass);
                if (item is null) continue;
                yield return item;
                if (++n >= limit) yield break;
            }
            var page = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (page is null || page.Cookie.Length == 0) yield break;
            paging.Cookie = page.Cookie;
        }
    }

    private static DirectoryEntryData? Map(SearchResultEntry e, string objectClass)
    {
        string? S(string n) => e.Attributes.Contains(n) && e.Attributes[n].Count > 0 ? e.Attributes[n][0]?.ToString() : null;
        var guidBytes = e.Attributes.Contains("objectGUID") ? e.Attributes["objectGUID"].GetValues(typeof(byte[])).Cast<byte[]>().FirstOrDefault() : null;
        if (guidBytes is not { Length: 16 }) return null;
        DateTimeOffset? FileTime(string n) => long.TryParse(S(n), out var ft) && ft > 0 && ft != long.MaxValue ? DateTimeOffset.FromFileTime(ft) : null;
        DateTimeOffset? Generalized(string n) =>
            S(n) is { Length: >= 14 } g && DateTime.TryParseExact(g[..14], "yyyyMMddHHmmss", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
                ? new DateTimeOffset(d, TimeSpan.Zero) : null;
        bool? enabled = int.TryParse(S("userAccountControl"), out var uac) ? (uac & 2) == 0 : null;
        var memberOf = e.Attributes.Contains("memberOf") ? e.Attributes["memberOf"].GetValues(typeof(string)).Cast<string>().Take(200).ToArray() : [];
        return new DirectoryEntryData(new Guid(guidBytes), objectClass, e.DistinguishedName, S("name"), S("sAMAccountName"), S("displayName"),
            S("mail"), S("userPrincipalName"), S("dNSHostName"), S("operatingSystem"), S("operatingSystemVersion"),
            objectClass is "computer" or "user" ? enabled : null, FileTime("lastLogonTimestamp"), Generalized("whenCreated"), FileTime("pwdLastSet"),
            S("department"), S("title"), S("description"), memberOf);
    }

    public static string? ParentDn(string dn)
    {
        // Split at the first unescaped comma.
        for (var i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '\\') { i++; continue; }
            if (dn[i] == ',') return dn[(i + 1)..];
        }
        return null;
    }

    public static string DomainFromDn(string baseDn) =>
        string.Join('.', baseDn.Split(',').Select(p => p.Trim()).Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p[3..]));
}

public sealed class DirectoryException(string message) : Exception(message);
