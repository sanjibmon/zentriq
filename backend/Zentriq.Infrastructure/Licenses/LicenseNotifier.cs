using System.Net;
using System.Text;
using Npgsql;
using Zentriq.Infrastructure.Mail;

namespace Zentriq.Infrastructure.Licenses;

/// <summary>
/// Tells administrators about new licence and web app alerts: an in-app notification (bell) for users who can see the
/// area, and one e-mail digest to the recipients in the settings. Every alert is announced once; integration channels
/// (Teams, Slack, ticketing…) receive the same alerts as integration events.
/// </summary>
public sealed class LicenseNotifier(NpgsqlDataSource ds, MailService mail)
{
    public sealed record Sent(int Alerts, int InApp, int Mailed);

    private sealed record Row(long Id, string Kind, string Severity, string Title, string? Detail, string? Link);

    public static bool IsWebApp(string kind) => kind.StartsWith("webapp_", StringComparison.Ordinal);

    public async Task<Sent> RunAsync(CancellationToken ct)
    {
        var settings = await LicenseSettings.LoadAsync(ds, ct);
        var rows = new List<Row>();
        await using (var cmd = ds.CreateCommand("""
            SELECT id, kind, severity, title, detail, link FROM zentriq.sam_alerts
             WHERE notified_at IS NULL AND resolved_at IS NULL AND NOT dismissed
             ORDER BY array_position(ARRAY['critical','high','medium','low','info'], severity), first_at LIMIT 500
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                rows.Add(new Row(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        if (rows.Count == 0) return new Sent(0, 0, 0);

        // Information (possible savings) is shown in the console only.
        var announce = rows.Where(r => r.Severity != "info").ToList();
        var inApp = 0;
        if (settings.NotifyInApp && announce.Count > 0)
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            foreach (var group in announce.GroupBy(r => IsWebApp(r.Kind)))
            {
                var perm = group.Key ? "webapps.view" : "licenses.view";
                var list = group.ToList();
                foreach (var a in list.Take(15))
                {
                    await using var cmd = new NpgsqlCommand("INSERT INTO zentriq.notifications (user_id, kind, title, body, link, permission) VALUES (NULL, @k, @t, @b, @l, @p)", conn);
                    cmd.Parameters.AddWithValue("k", a.Severity is "high" or "critical" ? "crit" : a.Severity == "medium" ? "warn" : "info");
                    cmd.Parameters.AddWithValue("t", a.Title.Length > 300 ? a.Title[..300] : a.Title);
                    cmd.Parameters.AddWithValue("b", a.Detail ?? "");
                    cmd.Parameters.AddWithValue("l", (object?)a.Link ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("p", perm);
                    await cmd.ExecuteNonQueryAsync(ct);
                    inApp++;
                }
                if (list.Count > 15)
                {
                    await using var more = new NpgsqlCommand("INSERT INTO zentriq.notifications (user_id, kind, title, body, link, permission) VALUES (NULL, 'warn', @t, '', @l, @p)", conn);
                    more.Parameters.AddWithValue("t", $"{list.Count - 15} more {(group.Key ? "web app" : "licence")} alert(s)");
                    more.Parameters.AddWithValue("l", group.Key ? "/modules/webapps?tab=alerts" : "/modules/licenses?tab=alerts");
                    more.Parameters.AddWithValue("p", perm);
                    await more.ExecuteNonQueryAsync(ct);
                    inApp++;
                }
            }
        }

        var mailed = 0;
        var to = settings.NotifyEmails.Where(e => e.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        if (to.Count > 0 && announce.Count > 0)
        {
            var smtp = await mail.LoadAsync(ct);
            if (MailService.IsUsable(smtp))
            {
                var baseUrl = smtp.ConsoleUrl?.TrimEnd('/');
                var subject = announce.Count == 1 ? $"Zentriq: {announce[0].Title}" : $"Zentriq: {announce.Count} new software licence and web app alerts";
                var text = new StringBuilder();
                var html = new StringBuilder("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1b2a35\"><h2 style=\"font-size:17px\">Software licences and web apps</h2><table style=\"border-collapse:collapse\">");
                foreach (var a in announce)
                {
                    var link = baseUrl is null || a.Link is null ? null : baseUrl + a.Link;
                    text.AppendLine($"[{a.Severity.ToUpperInvariant()}] {a.Title}");
                    if (a.Detail is not null) text.AppendLine("    " + a.Detail);
                    if (link is not null) text.AppendLine("    " + link);
                    var color = a.Severity is "high" or "critical" ? "#b42318" : a.Severity == "medium" ? "#b54708" : "#475467";
                    html.Append($"<tr><td style=\"padding:6px 10px;color:{color};font-weight:600;vertical-align:top\">{WebUtility.HtmlEncode(a.Severity)}</td><td style=\"padding:6px 10px\">")
                        .Append(link is null ? WebUtility.HtmlEncode(a.Title) : $"<a href=\"{WebUtility.HtmlEncode(link)}\">{WebUtility.HtmlEncode(a.Title)}</a>")
                        .Append(a.Detail is null ? "" : $"<div style=\"color:#475467\">{WebUtility.HtmlEncode(a.Detail)}</div>")
                        .Append("</td></tr>");
                }
                html.Append("</table><p style=\"color:#667085;font-size:12px\">Sent by Zentriq (Software License Management). Change the recipients under Software Licenses › Settings.</p></div>");
                foreach (var addr in to)
                {
                    try { await mail.SendAsync(smtp, addr, subject, text.ToString(), html.ToString(), ct); mailed++; }
                    catch (Exception ex) when (ex is MailException or System.Net.Mail.SmtpException or InvalidOperationException) { /* next recipient; the alerts stay in the console */ }
                }
            }
        }

        await using (var done = ds.CreateCommand("UPDATE zentriq.sam_alerts SET notified_at = now() WHERE id = ANY (@ids)"))
        {
            done.Parameters.AddWithValue("ids", rows.Select(r => r.Id).ToArray());
            await done.ExecuteNonQueryAsync(ct);
        }
        return new Sent(rows.Count, inApp, mailed);
    }
}
