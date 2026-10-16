using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zentriq.Infrastructure.Mail;

namespace Zentriq.Infrastructure.Integrations;

/// <summary>
/// Sends an event to a channel: signed JSON webhook, Slack, Microsoft Teams (Adaptive Card), e-mail, syslog (CEF or
/// JSON over UDP/TCP), a ServiceNow incident or a Jira issue. Formatting is static and side-effect free (tested).
/// </summary>
public sealed class ChannelSender(MailService mail)
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = false,
    })
    { Timeout = TimeSpan.FromSeconds(25) };

    private static readonly JsonSerializerOptions JsonOut = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<SendResult> SendAsync(Channel ch, OutboundEvent ev, CancellationToken ct)
    {
        try
        {
            return ch.Kind switch
            {
                "webhook" => await WebhookAsync(ch, ev, ct),
                "slack" => await PostJsonAsync(ch.Secrets.Url, SlackBody(ev), null, ct),
                "teams" => await PostJsonAsync(ch.Secrets.Url, TeamsBody(ev), null, ct),
                "email" => await EmailAsync(ch, ev, ct),
                "syslog" => await SyslogAsync(ch, ev, ct),
                "servicenow" => await ServiceNowAsync(ch, ev, ct),
                "jira" => await JiraAsync(ch, ev, ct),
                _ => new SendResult(false, null, $"Unknown channel type '{ch.Kind}'.", Permanent: true),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new SendResult(false, null, "Timed out."); }
        catch (HttpRequestException ex) { return new SendResult(false, null, Short(ex.InnerException?.Message ?? ex.Message)); }
        catch (SocketException ex) { return new SendResult(false, null, Short(ex.Message)); }
        catch (MailException ex) { return new SendResult(false, null, Short(ex.Message)); }
        catch (IOException ex) { return new SendResult(false, null, Short(ex.Message)); }
    }

    // ---- webhook ----------------------------------------------------------------------------------------

    public static string Sign(string secret, long timestamp, string body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(h.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{body}")));
    }

    public static string WebhookBody(OutboundEvent ev) => JsonSerializer.Serialize(ev, JsonOut);

    private static async Task<SendResult> WebhookAsync(Channel ch, OutboundEvent ev, CancellationToken ct)
    {
        var url = ch.Settings.Url;
        if (!ValidUrl(url, allowHttp: true)) return new SendResult(false, null, "The webhook URL is not valid.", Permanent: true);
        var body = WebhookBody(ev);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        req.Headers.Add("X-Zentriq-Event", ev.Type);
        req.Headers.Add("X-Zentriq-Delivery", ev.Id.ToString(CultureInfo.InvariantCulture));
        req.Headers.Add("X-Zentriq-Timestamp", ts.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(ch.Secrets.SigningSecret)) req.Headers.Add("X-Zentriq-Signature", Sign(ch.Secrets.SigningSecret, ts, body));
        if (!string.IsNullOrWhiteSpace(ch.Settings.HeaderName) && !string.IsNullOrEmpty(ch.Secrets.HeaderValue))
            req.Headers.TryAddWithoutValidation(ch.Settings.HeaderName.Trim(), ch.Secrets.HeaderValue);
        return await SendHttpAsync(req, ct);
    }

    // ---- Slack / Teams ------------------------------------------------------------------------------------

    private static string Badge(string severity) => severity.ToUpperInvariant();
    private static string Emoji(string severity) => severity switch { "critical" => "🔴", "high" => "🟠", "medium" => "🟡", "low" => "🔵", _ => "⚪" };

    public static JsonObject SlackBody(OutboundEvent ev)
    {
        var text = $"{Emoji(ev.Severity)} *[{Badge(ev.Severity)}] {SlackEscape(ev.Title)}*";
        if (!string.IsNullOrWhiteSpace(ev.Text)) text += "\n" + SlackEscape(Truncate(ev.Text, 2800));
        var context = new JsonArray(new JsonObject { ["type"] = "mrkdwn", ["text"] = $"Zentriq · {ev.Type} · {ev.OccurredAt:yyyy-MM-dd HH:mm} UTC" });
        if (ev.Link is not null) context.Add(new JsonObject { ["type"] = "mrkdwn", ["text"] = $"<{ev.Link}|Open in Zentriq>" });
        return new JsonObject
        {
            ["text"] = $"[{Badge(ev.Severity)}] {ev.Title}",
            ["blocks"] = new JsonArray(
                new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = text } },
                new JsonObject { ["type"] = "context", ["elements"] = context }),
        };
    }

    private static string SlackEscape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Teams "Workflows" / incoming webhook message with an Adaptive Card 1.4.</summary>
    public static JsonObject TeamsBody(OutboundEvent ev)
    {
        var color = ev.Severity switch { "critical" or "high" => "Attention", "medium" => "Warning", "low" => "Accent", _ => "Default" };
        var facts = new JsonArray(
            new JsonObject { ["title"] = "Severity", ["value"] = Badge(ev.Severity) },
            new JsonObject { ["title"] = "Event", ["value"] = ev.Type },
            new JsonObject { ["title"] = "Time", ["value"] = ev.OccurredAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) });
        if (ev.Asset?.Hostname is { } host) facts.Add(new JsonObject { ["title"] = "Device", ["value"] = host });
        var body = new JsonArray(
            new JsonObject { ["type"] = "TextBlock", ["text"] = ev.Title, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true, ["color"] = color },
            new JsonObject { ["type"] = "FactSet", ["facts"] = facts });
        if (!string.IsNullOrWhiteSpace(ev.Text)) body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = Truncate(ev.Text, 3000), ["wrap"] = true });
        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
        };
        if (ev.Link is not null)
            card["actions"] = new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open in Zentriq", ["url"] = ev.Link });
        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["contentUrl"] = null, ["content"] = card }),
        };
    }

    private static async Task<SendResult> PostJsonAsync(string? url, JsonNode body, AuthenticationHeaderValue? auth, CancellationToken ct)
    {
        if (!ValidUrl(url, allowHttp: false)) return new SendResult(false, null, "The webhook URL is missing or is not an https:// address.", Permanent: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = auth;
        return await SendHttpAsync(req, ct);
    }

    // ---- e-mail ---------------------------------------------------------------------------------------------

    public static (string Subject, string Body) EmailContent(OutboundEvent ev)
    {
        var subject = $"[Zentriq][{Badge(ev.Severity)}] {Truncate(ev.Title, 180)}";
        var sb = new StringBuilder();
        sb.AppendLine(ev.Title).AppendLine();
        if (!string.IsNullOrWhiteSpace(ev.Text)) sb.AppendLine(ev.Text).AppendLine();
        sb.AppendLine($"Severity: {ev.Severity}");
        sb.AppendLine($"Event:    {ev.Type}");
        sb.AppendLine($"Time:     {ev.OccurredAt:yyyy-MM-dd HH:mm} UTC");
        if (ev.Asset?.Hostname is { } h) sb.AppendLine($"Device:   {h}{(ev.Asset.Ip is { } ip ? $" ({ip})" : "")}");
        if (ev.Link is not null) sb.AppendLine().AppendLine(ev.Link);
        sb.AppendLine().AppendLine("— Zentriq alert rule notification. Rules are managed in Integrations & Automation.");
        return (subject, sb.ToString());
    }

    private async Task<SendResult> EmailAsync(Channel ch, OutboundEvent ev, CancellationToken ct)
    {
        if (ch.Settings.Recipients.Count == 0) return new SendResult(false, null, "No recipients are set.", Permanent: true);
        var smtp = await mail.LoadAsync(ct);
        if (!MailService.IsUsable(smtp)) return new SendResult(false, null, "E-mail (SMTP) is not configured in System Settings.");
        var (subject, body) = EmailContent(ev);
        foreach (var to in ch.Settings.Recipients) await mail.SendAsync(smtp, to, subject, body, null, ct);
        return new SendResult(true, $"sent to {ch.Settings.Recipients.Count} recipient(s)", null);
    }

    // ---- syslog -----------------------------------------------------------------------------------------------

    private static int CefSeverity(string s) => s switch { "critical" => 10, "high" => 8, "medium" => 5, "low" => 3, _ => 1 };
    private static int SyslogSeverity(string s) => s switch { "critical" => 2, "high" => 3, "medium" => 4, "low" => 5, _ => 6 };
    private static string CefHeader(string s) => s.Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    private static string CefExt(string s) => s.Replace("\\", "\\\\").Replace("=", "\\=").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>RFC 5424 syslog line (facility local0) carrying CEF or JSON.</summary>
    public static string SyslogLine(OutboundEvent ev, string format, string version, string hostName)
    {
        var pri = 16 * 8 + SyslogSeverity(ev.Severity);
        var ts = ev.OccurredAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        string msg;
        if (format == "json") msg = WebhookBody(ev);
        else
        {
            var ext = new List<string>
            {
                $"rt={ev.OccurredAt.ToUnixTimeMilliseconds()}",
                $"externalId={ev.Id}",
                $"msg={CefExt(Truncate(ev.Text, 1500))}",
            };
            if (ev.Asset?.Hostname is { } h) ext.Add($"dhost={CefExt(h)}");
            if (ev.Asset?.Ip is { } ip) ext.Add($"dst={CefExt(ip)}");
            if (ev.Link is not null) { ext.Add("cs1Label=link"); ext.Add($"cs1={CefExt(ev.Link)}"); }
            if (ev.Asset?.Criticality is { } c) { ext.Add("cs2Label=assetCriticality"); ext.Add($"cs2={CefExt(c)}"); }
            msg = $"CEF:0|Zentriq|Zentriq|{CefHeader(version)}|{CefHeader(ev.Type)}|{CefHeader(Truncate(ev.Title, 512))}|{CefSeverity(ev.Severity)}|{string.Join(' ', ext)}";
        }
        return $"<{pri}>1 {ts} {Sanitize(hostName)} Zentriq - {Sanitize(ev.Type)} - {msg}";
    }

    private static string Sanitize(string s) => string.IsNullOrWhiteSpace(s) ? "-" : new string(s.Where(ch => ch > 32 && ch < 127).Take(48).ToArray());

    private static async Task<SendResult> SyslogAsync(Channel ch, OutboundEvent ev, CancellationToken ct)
    {
        var host = ch.Settings.Host?.Trim();
        if (string.IsNullOrEmpty(host)) return new SendResult(false, null, "No syslog server is set.", Permanent: true);
        var port = ch.Settings.Port is > 0 and < 65536 ? ch.Settings.Port.Value : 514;
        var line = SyslogLine(ev, ch.Settings.Format == "json" ? "json" : "cef", typeof(ChannelSender).Assembly.GetName().Version?.ToString(3) ?? "1.0", Environment.MachineName);
        var bytes = Encoding.UTF8.GetBytes(line);
        if (ch.Settings.Protocol == "tcp")
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(host, port, cts.Token);
            await using var stream = tcp.GetStream();
            await stream.WriteAsync(bytes.Concat("\n"u8.ToArray()).ToArray(), cts.Token);
            await stream.FlushAsync(cts.Token);
        }
        else
        {
            using var udp = new UdpClient();
            if (bytes.Length > 8000) bytes = bytes[..8000];
            await udp.SendAsync(bytes, host, port, ct);
        }
        return new SendResult(true, $"{(ch.Settings.Protocol == "tcp" ? "TCP" : "UDP")} {host}:{port}", null);
    }

    // ---- ServiceNow ---------------------------------------------------------------------------------------------

    public static JsonObject ServiceNowBody(Channel ch, OutboundEvent ev, string correlationId)
    {
        var level = ev.Severity switch { "critical" or "high" => "1", "medium" => "2", _ => "3" };
        var o = new JsonObject
        {
            ["short_description"] = Truncate($"[Zentriq] {ev.Title}", 160),
            ["description"] = DescriptionText(ev),
            ["urgency"] = level,
            ["impact"] = level,
            ["correlation_id"] = correlationId,
            ["correlation_display"] = "Zentriq",
        };
        if (!string.IsNullOrWhiteSpace(ch.Settings.AssignmentGroup)) o["assignment_group"] = ch.Settings.AssignmentGroup.Trim();
        if (!string.IsNullOrWhiteSpace(ch.Settings.Category)) o["category"] = ch.Settings.Category.Trim();
        return o;
    }

    private static string DescriptionText(OutboundEvent ev)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(ev.Text)) sb.AppendLine(Truncate(ev.Text, 20000)).AppendLine();
        sb.AppendLine($"Severity: {ev.Severity}").AppendLine($"Event: {ev.Type}").AppendLine($"Time: {ev.OccurredAt:yyyy-MM-dd HH:mm} UTC");
        if (ev.Asset?.Hostname is { } h) sb.AppendLine($"Device: {h}{(ev.Asset.Ip is { } ip ? $" ({ip})" : "")}");
        if (ev.Link is not null) sb.AppendLine($"Zentriq: {ev.Link}");
        return sb.ToString();
    }

    private static async Task<SendResult> ServiceNowAsync(Channel ch, OutboundEvent ev, CancellationToken ct)
    {
        var baseUrl = ch.Settings.InstanceUrl?.Trim().TrimEnd('/');
        if (!ValidUrl(baseUrl, allowHttp: false)) return new SendResult(false, null, "The ServiceNow instance URL must be an https:// address.", Permanent: true);
        var table = string.IsNullOrWhiteSpace(ch.Settings.Table) ? "incident" : ch.Settings.Table.Trim();
        if (!table.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return new SendResult(false, null, "The table name is not valid.", Permanent: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/now/table/{table}?sysparm_fields=number,sys_id")
        {
            Content = new StringContent(ServiceNowBody(ch, ev, $"zentriq-{ev.Id}").ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Authorization = Basic(ch.Settings.Username, ch.Secrets.Password);
        var r = await SendHttpAsync(req, ct, readBody: true);
        if (!r.Ok) return r;
        var number = TryJson(r.Response, "result", "number");
        return new SendResult(true, number is null ? "created" : $"created {number}", null, Ticket: number);
    }

    // ---- Jira ----------------------------------------------------------------------------------------------------

    public static JsonObject JiraBody(Channel ch, OutboundEvent ev)
    {
        var labels = new JsonArray("zentriq");
        foreach (var l in ch.Settings.Labels.Where(l => !string.IsNullOrWhiteSpace(l) && !l.Contains(' '))) labels.Add(l.Trim());
        return new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["project"] = new JsonObject { ["key"] = ch.Settings.ProjectKey?.Trim() },
                ["summary"] = Truncate($"[Zentriq] {ev.Title}", 250),
                ["description"] = DescriptionText(ev),
                ["issuetype"] = new JsonObject { ["name"] = string.IsNullOrWhiteSpace(ch.Settings.IssueType) ? "Task" : ch.Settings.IssueType.Trim() },
                ["labels"] = labels,
            },
        };
    }

    private static async Task<SendResult> JiraAsync(Channel ch, OutboundEvent ev, CancellationToken ct)
    {
        var baseUrl = ch.Settings.InstanceUrl?.Trim().TrimEnd('/');
        if (!ValidUrl(baseUrl, allowHttp: false)) return new SendResult(false, null, "The Jira URL must be an https:// address.", Permanent: true);
        if (string.IsNullOrWhiteSpace(ch.Settings.ProjectKey)) return new SendResult(false, null, "No Jira project key is set.", Permanent: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/rest/api/2/issue")
        {
            Content = new StringContent(JiraBody(ch, ev).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Authorization = ch.Settings.Auth == "bearer"
            ? new AuthenticationHeaderValue("Bearer", ch.Secrets.Password ?? "")
            : Basic(ch.Settings.Username, ch.Secrets.Password);
        var r = await SendHttpAsync(req, ct, readBody: true);
        if (!r.Ok) return r;
        var key = TryJson(r.Response, "key");
        return new SendResult(true, key is null ? "created" : $"created {key}", null, Ticket: key);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static AuthenticationHeaderValue Basic(string? user, string? password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user?.Trim()}:{password}")));

    public static bool ValidUrl(string? url, bool allowHttp) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || (allowHttp && u.Scheme == Uri.UriSchemeHttp)) && !string.IsNullOrEmpty(u.Host);

    private static async Task<SendResult> SendHttpAsync(HttpRequestMessage req, CancellationToken ct, bool readBody = false)
    {
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Zentriq", typeof(ChannelSender).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
        using var resp = await Http.SendAsync(req, ct);
        var code = (int)resp.StatusCode;
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode) return new SendResult(true, readBody ? text : $"HTTP {code}", null);
        var permanent = code is 400 or 401 or 403 or 404 or 405 or 410 or 422 || (code >= 300 && code < 400);
        var hint = code switch
        {
            401 => " (check the user name and password / token)",
            403 => " (the account is not allowed to do this)",
            404 => " (check the URL, table or project)",
            >= 300 and < 400 => $" (redirected to {resp.Headers.Location}; use the final URL)",
            _ => "",
        };
        return new SendResult(false, $"HTTP {code}", $"HTTP {code} {resp.ReasonPhrase}{hint}: {Short(text)}", Permanent: permanent);
    }

    private static string? TryJson(string? json, params string[] path)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var e = JsonDocument.Parse(json).RootElement;
            foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; }
            return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    private static string Short(string s) => Truncate(s.ReplaceLineEndings(" ").Trim(), 300);
}
