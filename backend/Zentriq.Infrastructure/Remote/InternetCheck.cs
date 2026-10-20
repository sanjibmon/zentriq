using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Zentriq.Infrastructure.Remote;

/// <summary>One resolved address of the internet name and whether it is a public (internet-routable) address.</summary>
public sealed record InternetAddress(string Ip, bool Public);

/// <summary>
/// Result of checking the internet address of remote support. <see cref="Ok"/> when the name resolves only to public
/// addresses and an HTTPS connection to it reaches this server's remote support engine (same certificate).
/// </summary>
public sealed record InternetCheckResult(
    string Host, int Port, bool Ok, bool Resolved, bool PublicAddress, bool Reachable, bool SameServer,
    IReadOnlyList<InternetAddress> Addresses, string Message, DateTimeOffset CheckedAt, bool ConfirmedFromOutside = false)
{
    /// <summary>Usable for invitations: verified, or confirmed by an administrator from outside the network.</summary>
    public bool Usable => Ok || (ConfirmedFromOutside && Resolved && PublicAddress);
}

/// <summary>
/// Checks that people on the internet can reach remote support at a public name or address over HTTPS:
/// 1. the name resolves; 2. every address is public (not 10/8, 172.16/12, 192.168/16, 100.64/10, link-local, loopback);
/// 3. a TLS connection to the address and port succeeds and presents the same certificate as the local engine.
/// Step 3 runs from the server itself; routers without NAT loopback refuse it even when the port is open from outside,
/// so an administrator can confirm reachability from outside instead (for example from a phone on mobile data).
/// </summary>
public static class InternetCheck
{
    public static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return false;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return !(a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal || a.IsIPv6Multicast || a.Equals(IPAddress.IPv6None));
        var b = a.GetAddressBytes();
        return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)       // carrier-grade NAT
            || (b[0] == 192 && b[1] == 0 && b[2] == 2) || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // documentation / benchmarking
            || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113));
    }

    /// <summary>Validates the address an administrator entered: a DNS name or public IPv4 address and an HTTPS port.</summary>
    public static string? Validate(string? host, int? port, out string normalized)
    {
        normalized = "";
        var raw = (host ?? "").Trim();
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "Remote support works over HTTPS only. Enter the name without http://.";
        if (raw.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(raw, UriKind.Absolute, out var u)) raw = u.Host;
        if (RemoteServerConfig.NormalizeHost(raw) is not { } h) return "Enter a public DNS name such as support.example.com, or your public IPv4 address.";
        if (IPAddress.TryParse(h, out var ip) && !IsPublic(ip)) return $"{h} is a private address; people on the internet cannot reach it. Enter your public IP address or a public DNS name.";
        if (!h.Contains('.')) return "Enter a full DNS name such as support.example.com (a short computer name only works inside your network).";
        if (port is 80) return "Port 80 is plain HTTP. Remote support works over HTTPS only: use 443 or 8446.";
        if (port is < 1 or > 65535) return "Enter a port between 1 and 65535.";
        normalized = h;
        return null;
    }

    public static async Task<InternetCheckResult> RunAsync(string host, int port, int enginePort, CancellationToken ct, bool confirmedFromOutside = false)
    {
        var now = DateTimeOffset.UtcNow;
        IPAddress[] ips;
        try
        {
            ips = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
        {
            return new(host, port, false, false, false, false, false, [], $"{host} does not resolve in DNS. Create a public DNS record for it (pointing to your public IP address), or enter the public IP address.", now, confirmedFromOutside);
        }
        ips = ips.Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).Distinct().ToArray();
        var list = ips.Select(a => new InternetAddress(a.ToString(), IsPublic(a))).ToList();
        if (list.Count == 0)
            return new(host, port, false, false, false, false, false, list, $"{host} has no IPv4 or IPv6 address in DNS.", now, confirmedFromOutside);
        var allPublic = list.All(a => a.Public);
        if (!allPublic)
        {
            var priv = string.Join(", ", list.Where(a => !a.Public).Select(a => a.Ip));
            return new(host, port, false, true, false, false, false, list,
                $"{host} resolves to {priv} on this server, which is not reachable from the internet. Point the public DNS record to your public IP address " +
                "(if your office DNS answers with the server's LAN address, that is fine for people inside, but check that public DNS returns the public address).", now, confirmedFromOutside);
        }

        var expected = await ThumbprintAsync(IPAddress.Loopback, enginePort, "localhost", ct);
        string? lastError = null;
        foreach (var ip in ips)
        {
            var thumb = await ThumbprintAsync(ip, port, host, ct, e => lastError = e);
            if (thumb is null) continue;
            var same = expected is not null && string.Equals(thumb, expected, StringComparison.OrdinalIgnoreCase);
            return same
                ? new(host, port, true, true, true, true, true, list, $"https://{host}:{port} reaches this server's remote support from {ip}. People on the internet can use invitation links.", now, confirmedFromOutside)
                : new(host, port, false, true, true, true, false, list, $"https://{host}:{port} answers, but it is not this server's remote support (another certificate). Forward TCP {port} on the router to this server, port {enginePort}.", now, confirmedFromOutside);
        }
        return new(host, port, false, true, true, false, false, list,
            $"https://{host}:{port} could not be reached ({lastError ?? "no answer"}). Forward TCP {port} on the router/firewall to this server, port {enginePort}, and allow it in the firewall. " +
            "Some routers cannot connect to their own public address from inside (no NAT loopback): if it opens from outside, for example from a phone on mobile data, confirm that below.", now, confirmedFromOutside);
    }

    /// <summary>Thumbprint of the certificate an HTTPS endpoint presents, or null when it cannot be reached.</summary>
    private static async Task<string?> ThumbprintAsync(IPAddress ip, int port, string sni, CancellationToken ct, Action<string>? error = null)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var tcp = new TcpClient(ip.AddressFamily);
            await tcp.ConnectAsync(ip, port, t.Token);
            X509Certificate? seen = null;
            await using var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = sni, RemoteCertificateValidationCallback = (_, c, _, _) => { seen = c; return true; } }, t.Token);
            return seen?.GetCertHashString();
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            error?.Invoke(ex is OperationCanceledException ? "timed out" : ex.Message);
            return null;
        }
    }
}
