using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Zentriq.Worker.Handlers;

public sealed record ProbeResult(bool Alive, int[] OpenPorts, int DurationMs, string? Note, string? Mac = null);

public interface IHostPinger
{
    Task<ProbeResult> ProbeAsync(IPAddress ip, IReadOnlyList<int> ports, TimeSpan timeout, CancellationToken ct);
    Task<string?> ReverseLookupAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct);
    Task<IPAddress?> ResolveAsync(string host, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Agentless reachability probe using TCP connects only (no raw sockets, no ICMP privileges needed).
/// A completed connect means the port is open; a refused connect (RST) also proves the host is up.
/// Timeouts on every port mean "unreachable" (host down, filtered, or not routed).
/// </summary>
public sealed class TcpHostPinger : IHostPinger
{
    public async Task<ProbeResult> ProbeAsync(IPAddress ip, IReadOnlyList<int> ports, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(ports.Select(p => ProbePortAsync(ip, p, timeout, ct)));
        var open = ports.Where((_, i) => results[i] == PortState.Open).ToArray();
        var alive = results.Any(r => r is PortState.Open or PortState.Refused);
        var note = open.Length == 0 && alive ? "Host refused connections on probed ports" : null;
        string? mac = null;
        if (!alive)
        {
            // Every TCP probe timed out. A host behind a firewall (e.g. Windows Firewall with the default "Public"
            // profile) still answers ARP on the local subnet, and often ICMP echo: report it as present but filtered
            // instead of "unreachable", so it is not silently missed.
            mac = OperatingSystem.IsWindows() ? ArpLookup(ip) : null;
            var pings = mac is null && await PingAsync(ip, timeout);
            if (mac is not null || pings)
            {
                alive = true;
                note = $"Host is up ({(mac is not null ? "answered ARP, MAC " + mac : "answers ping")}) but every probed port is blocked by a firewall. " +
                       "Install the Zentriq Agent on it (Intune, Configuration Manager, Group Policy or manually), or allow WinRM (TCP 5985) from the Zentriq server.";
            }
        }
        return new ProbeResult(alive, open, (int)sw.ElapsedMilliseconds, note, mac);
    }

    private static async Task<bool> PingAsync(IPAddress ip, TimeSpan timeout)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var r = await ping.SendPingAsync(ip, (int)Math.Clamp(timeout.TotalMilliseconds, 200, 3000));
            return r.Status == System.Net.NetworkInformation.IPStatus.Success;
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.PingException or InvalidOperationException) { return false; }
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macAddrLen);

    /// <summary>Resolves the MAC of an address on a directly connected subnet (Windows SendARP); null otherwise.</summary>
    private static string? ArpLookup(IPAddress ip)
    {
        try
        {
            if (!IsOnLocalSubnet(ip)) return null;
            var mac = new byte[6];
            uint len = (uint)mac.Length;
            var dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
            if (SendARP(dest, 0, mac, ref len) != 0 || len < 6) return null;
            return string.Join(":", mac.Take(6).Select(b => b.ToString("X2")));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private static bool IsOnLocalSubnet(IPAddress ip)
    {
        var target = ip.GetAddressBytes();
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.PrefixLength is 0 or 32) continue;
                var local = u.Address.GetAddressBytes();
                var bits = u.PrefixLength;
                var same = true;
                for (var i = 0; i < 4 && same; i++)
                {
                    var maskBits = Math.Clamp(bits - i * 8, 0, 8);
                    var mask = (byte)(0xFF << (8 - maskBits));
                    same = (local[i] & mask) == (target[i] & mask);
                }
                if (same) return true;
            }
        }
        return false;
    }

    private enum PortState { Open, Refused, NoResponse }

    private static async Task<PortState> ProbePortAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), cts.Token);
            return PortState.Open;
        }
        catch (SocketException se) when (se.SocketErrorCode == SocketError.ConnectionRefused) { return PortState.Refused; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return PortState.NoResponse; }
        catch (SocketException) { return PortState.NoResponse; }
    }

    public async Task<string?> ReverseLookupAsync(IPAddress ip, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var entry = await Dns.GetHostEntryAsync(ip.ToString(), cts.Token);
            return string.IsNullOrWhiteSpace(entry.HostName) || entry.HostName == ip.ToString() ? null : entry.HostName.ToLowerInvariant();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    public async Task<IPAddress?> ResolveAsync(string host, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cts.Token);
            return addrs.FirstOrDefault();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }
}
