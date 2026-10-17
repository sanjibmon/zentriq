using System.Net;
using System.Net.Security;
using System.Text.RegularExpressions;
using Zentriq.Inventory;

namespace Zentriq.Worker.Agentless;

/// <summary>
/// Reads what a device's web management interface says about itself (page title, Server header, certificate names)
/// without signing in. Printers, firewalls, switches, cameras and NAS devices name themselves there. Read-only GET of
/// "/" with a short timeout; redirects are followed on the same host only.
/// </summary>
public static partial class WebFingerprinter
{
    public sealed record WebInfo(string Url, string? Title, string? Server, string? CertSubject)
    {
        public string Text => string.Join(" | ", new[] { Title, Server, CertSubject }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static readonly int[][] Candidates = [[443, 1], [80, 0], [8443, 1], [8080, 0], [4443, 1], [10443, 1]];

    /// <summary>The first web interface that identifies the device, else the first one that answered.</summary>
    public static async Task<WebInfo?> ProbeAsync(IPAddress ip, IReadOnlyCollection<int> openPorts, TimeSpan timeout, CancellationToken ct)
    {
        WebInfo? first = null;
        foreach (var c in Candidates)
        {
            if (!openPorts.Contains(c[0])) continue;
            var info = await ProbePortAsync(ip, c[0], c[1] == 1, timeout, ct);
            if (info is null) continue;
            if (Recognise(info) is not null) return info;
            first ??= info;
        }
        return first;
    }

    private static async Task<WebInfo?> ProbePortAsync(IPAddress ip, int port, bool https, TimeSpan timeout, CancellationToken ct)
    {
        foreach (var c in new[] { new[] { port, https ? 1 : 0 } })
        {
            string? certSubject = null;
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 3,
                ConnectTimeout = timeout,
                UseCookies = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    // Management interfaces use self-signed certificates; only the names are read, nothing is sent.
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    {
                        if (cert is not null) certSubject = CertNames(new System.Security.Cryptography.X509Certificates.X509Certificate2(cert));
                        return true;
                    },
                },
            };
            using var http = new HttpClient(handler) { Timeout = timeout + timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Zentriq-Discovery/1.0");
            var host = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
            var url = $"{(https ? "https" : "http")}://{host}:{c[0]}/";
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                var server = resp.Headers.Server.Count > 0 ? resp.Headers.Server.ToString() : null;
                if (resp.Headers.TryGetValues("WWW-Authenticate", out var auth)) server = string.Join(" ", new[] { server }.Concat(auth).Where(s => s is not null));
                string? title = null;
                if (resp.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) != false)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    var buf = new byte[65536];
                    var n = 0;
                    int read;
                    while (n < buf.Length && (read = await stream.ReadAsync(buf.AsMemory(n), ct)) > 0) n += read;
                    title = TitleOf(System.Text.Encoding.UTF8.GetString(buf, 0, n));
                }
                var info = new WebInfo(url, title, server, certSubject);
                if (info.Text.Length > 0) return info;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
            {
                if (certSubject is not null) return new WebInfo(url, null, null, certSubject);
            }
        }
        return null;
    }

    public static string? TitleOf(string html)
    {
        var m = TitleRegex().Match(html);
        if (!m.Success) return null;
        var t = WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, @"\s+", " ")).Trim();
        return t.Length is > 0 and <= 200 ? t : null;
    }

    private static string? CertNames(System.Security.Cryptography.X509Certificates.X509Certificate2 c)
    {
        var parts = new List<string> { c.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) };
        var o = c.SubjectName.Name.Split(',').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("O=", StringComparison.Ordinal) || p.StartsWith("OU=", StringComparison.Ordinal));
        if (o is not null) parts.Add(o[(o.IndexOf('=') + 1)..]);
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>Device type, manufacturer and model from a web interface.</summary>
    public static Fingerprint? Recognise(WebInfo? web)
    {
        if (web is null) return null;
        var fp = DeviceFingerprint.FromText(web.Text, "web interface");
        if (fp is null) return null;
        fp.Model = DeviceFingerprint.ModelFromTitle(web.Title);
        return fp;
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();
}
