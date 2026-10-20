using Zentriq.Infrastructure.Remote;

namespace Zentriq.Worker.Remote;

/// <summary>Every 2 minutes: reads the remote support agents (online state, names), maps them to assets and updates quick support sessions.</summary>
public sealed class RemoteSupportSync(RemoteSupportService remote, ILogger<RemoteSupportSync> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); } catch (OperationCanceledException) { return; }
        string? lastError = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (remote.Configured)
            {
                try
                {
                    var r = await remote.SyncAsync(stoppingToken);
                    await remote.RefreshQuickAsync(stoppingToken); // quick support sessions: joined, closed, expired
                    if (lastError is not null) logger.LogInformation("Remote support reachable again: {Nodes} device(s), {Online} online", r.Nodes, r.Connected);
                    lastError = null;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // Log a failure once (the service may still be starting), not every cycle.
                    if (lastError != ex.Message) logger.LogWarning("Remote support sync failed: {Error}", ex.Message);
                    lastError = ex.Message;
                }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
