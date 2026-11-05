using System.Globalization;
using System.Text;

namespace Zentriq.Infrastructure.WebApps;

/// <summary>Reads the user list a web app's admin console exports (CSV) into accounts.</summary>
public static class AccountCsv
{
    public sealed record Row(string UserName, string? DisplayName, string? Email, string Role, string Status, DateTimeOffset? LastLogin, string? LicenseTier, decimal? Cost, string? Department);

    /// <summary>
    /// Accounts from a CSV user export. The header names the columns: user / username / login / email (required), name,
    /// email, role, status, last login, plan / tier / licence, cost, department. Up to 20,000 rows.
    /// </summary>
    public static (List<Row> Rows, string? Error) Parse(string csv)
    {
        var lines = csv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return (new List<Row>(), "The file needs a header row and at least one account.");
        var sep = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : lines[0].Contains('\t') && !lines[0].Contains(',') ? '\t' : ',';
        var head = Split(lines[0], sep).Select(h => new string(h.ToLowerInvariant().Where(char.IsLetter).ToArray())).ToList();
        int Col(params string[] names) => head.FindIndex(h => names.Contains(h));
        var user = Col("user", "username", "login", "userprincipalname", "upn", "account", "emailaddress", "email", "primaryemail", "mail");
        if (user < 0) return (new List<Row>(), "No user column: name one column user, username, login or email.");
        int name = Col("name", "displayname", "fullname"), email = Col("email", "emailaddress", "mail", "primaryemail"), role = Col("role", "type", "accounttype", "usertype"),
            status = Col("status", "state", "accountstatus"), last = Col("lastlogin", "lastsignin", "lastlogintime", "lastsignintime", "lastactive", "lastactivity", "lastseen"),
            tier = Col("plan", "tier", "license", "licence", "licensetype", "seat", "product", "edition"), cost = Col("cost", "price", "monthlycost", "annualcost"),
            dept = Col("department", "dept", "team", "orgunit", "orgunitpath");
        var rows = new List<Row>();
        foreach (var line in lines.Skip(1).Take(20_000))
        {
            var f = Split(line, sep);
            string? V(int i) => i >= 0 && i < f.Count && f[i].Trim().Length > 0 ? f[i].Trim() : null;
            var u = V(user);
            if (u is null) continue;
            var r = (V(role) ?? "").ToLowerInvariant();
            var st = (V(status) ?? "").ToLowerInvariant();
            rows.Add(new Row(u, V(name), V(email) ?? (u.Contains('@') ? u : null),
                r.Contains("owner") ? "owner" : r.Contains("admin") ? "admin" : r.Contains("guest") || r.Contains("external") ? "guest" : r.Contains("service") || r.Contains("bot") ? "service" : "user",
                st.Length == 0 || st is "active" or "enabled" or "yes" or "true" ? "active" : st.Contains("invit") || st.Contains("pending") ? "invited" : st.Contains("remov") || st.Contains("delet") ? "removed" : "disabled",
                DateTimeOffset.TryParse(V(last), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ll) ? ll : null,
                V(tier), decimal.TryParse(V(cost)?.TrimStart('$', '€', '£', '₹'), NumberStyles.Number, CultureInfo.InvariantCulture, out var c) && c >= 0 ? c : null, V(dept)));
        }
        rows = rows.GroupBy(r => r.UserName.ToLowerInvariant()).Select(g => g.Last()).ToList();
        return rows.Count == 0 ? (new List<Row>(), "No accounts found in the file.") : (rows, null);
    }

    private static List<string> Split(string line, char sep)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { list.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        list.Add(sb.ToString());
        return list;
    }
}
