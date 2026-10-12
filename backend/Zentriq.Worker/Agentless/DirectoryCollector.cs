using System.DirectoryServices.Protocols;
using System.Net;

namespace Zentriq.Worker.Agentless;

public sealed record DirectoryComputer(string Name, string? DnsHostName, string? OperatingSystem, Guid ObjectGuid, DateTimeOffset? LastLogon, string DistinguishedName);

/// <summary>
/// Active Directory discovery: lists enabled computer objects under a domain or OU with a read-only
/// account over LDAPS (636) or LDAP with Kerberos/NTLM signing and sealing (389). An AD object is a
/// candidate target only; it is never treated as proof that the computer is online or inventoried.
/// </summary>
public static class DirectoryCollector
{
    private const string EnabledComputers = "(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";

    public static List<DirectoryComputer> ListComputers(string server, string baseDn, bool ldaps, ScanCredential cred, int limit, TimeSpan timeout)
    {
        var (domain, user) = cred.SplitUser();
        using var conn = new LdapConnection(new LdapDirectoryIdentifier(server, ldaps ? 636 : 389), new NetworkCredential(user, cred.Secret.Password, domain), AuthType.Negotiate)
        {
            Timeout = timeout,
        };
        conn.SessionOptions.ProtocolVersion = 3;
        conn.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (ldaps) conn.SessionOptions.SecureSocketLayer = true;
        else { conn.SessionOptions.Signing = true; conn.SessionOptions.Sealing = true; }
        conn.Bind();

        var result = new List<DirectoryComputer>();
        var paging = new PageResultRequestControl(500);
        var request = new SearchRequest(baseDn, EnabledComputers, SearchScope.Subtree,
            "name", "dNSHostName", "operatingSystem", "objectGUID", "lastLogonTimestamp", "distinguishedName");
        request.Controls.Add(paging);
        while (true)
        {
            var response = (SearchResponse)conn.SendRequest(request);
            foreach (SearchResultEntry e in response.Entries)
            {
                string? Attr(string n) => e.Attributes.Contains(n) && e.Attributes[n].Count > 0 ? e.Attributes[n][0]?.ToString() : null;
                var guidBytes = e.Attributes.Contains("objectGUID") ? e.Attributes["objectGUID"].GetValues(typeof(byte[])).Cast<byte[]>().FirstOrDefault() : null;
                DateTimeOffset? lastLogon = long.TryParse(Attr("lastLogonTimestamp"), out var ft) && ft > 0 ? DateTimeOffset.FromFileTime(ft) : null;
                result.Add(new DirectoryComputer(Attr("name") ?? "", Attr("dNSHostName"), Attr("operatingSystem"),
                    guidBytes is { Length: 16 } ? new Guid(guidBytes) : Guid.Empty, lastLogon, e.DistinguishedName));
                if (result.Count >= limit) return result;
            }
            var pageResponse = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (pageResponse is null || pageResponse.Cookie.Length == 0) break;
            paging.Cookie = pageResponse.Cookie;
        }
        return result;
    }
}
