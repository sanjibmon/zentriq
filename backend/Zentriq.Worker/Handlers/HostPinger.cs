using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Zentriq.Worker.Handlers;

public sealed record ProbeResult(bool Alive, int[] OpenPorts, int DurationMs, string? Note);

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
        return new ProbeResult(alive, open, (int)sw.ElapsedMilliseconds, note);
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
