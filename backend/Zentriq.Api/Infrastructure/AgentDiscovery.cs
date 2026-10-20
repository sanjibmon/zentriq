using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Zentriq.Api.Infrastructure;

/// <summary>
/// LAN discovery for Zentriq Agents (UDP, same port number as the agent port, 8444 by default). An agent that cannot
/// reach the server at any address it knows (for example after the server got a new IP address from DHCP) broadcasts
/// "ZENTRIQ-DISCOVER/1" on its network; the server answers with its agent addresses. The agent then connects only if the
/// server presents the key it has pinned, so a forged answer cannot redirect it. Nothing secret is sent.
/// </summary>
public sealed class AgentDiscovery(AgentServer server, ILogger<AgentDiscovery> logger) : BackgroundService
{
    public const string Request = "ZENTRIQ-DISCOVER/1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!server.Enabled) return;
        UdpClient udp;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, server.Port));
        }
        catch (SocketException ex)
        {
            logger.LogWarning("Agent discovery is not available (UDP {Port}: {Message}). Agents still connect to the addresses they know.", server.Port, ex.Message);
            return;
        }
        logger.LogInformation("Agent discovery listening on UDP {Port}.", server.Port);
        var recent = new Dictionary<IPAddress, DateTime>();
        using (udp)
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult msg;
                try { msg = await udp.ReceiveAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                if (msg.Buffer.Length > 256 || !Encoding.ASCII.GetString(msg.Buffer).StartsWith(Request, StringComparison.Ordinal)) continue;
                // At most one answer per device every 5 seconds.
                var now = DateTime.UtcNow;
                if (recent.TryGetValue(msg.RemoteEndPoint.Address, out var last) && now - last < TimeSpan.FromSeconds(5)) continue;
                recent[msg.RemoteEndPoint.Address] = now;
                if (recent.Count > 1000) recent.Clear();
                var answer = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, urls = server.AlternateUrls(), pins = server.ServerPin is { } p ? new[] { p } : [] });
                try { await udp.SendAsync(answer, msg.RemoteEndPoint, stoppingToken); }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException) { }
            }
    }
}
