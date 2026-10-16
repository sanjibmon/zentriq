using Zentriq.Infrastructure.Integrations;

namespace Zentriq.Worker.Integrations;

/// <summary>
/// Every 30 seconds: runs the event detectors (once a minute), routes new events to alert rules and sends due
/// deliveries (webhooks, chat, e-mail, syslog, tickets) with retries. Runs in every worker; rows are claimed with
/// SKIP LOCKED and detector windows are locked per detector, so several workers never send the same message twice.
/// </summary>
public sealed class IntegrationService(IntegrationStore store, ILogger<IntegrationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (tick % 2 == 0)
                {
                    var found = await store.DetectAsync(stoppingToken);
                    if (found > 0) logger.LogInformation("Integrations: {Count} new event(s)", found);
                }
                var queued = await store.RouteAsync(stoppingToken);
                if (queued > 0) logger.LogInformation("Integrations: {Count} notification(s) queued", queued);
                var (sent, failed) = await store.DeliverDueAsync(stoppingToken);
                if (sent + failed > 0) logger.LogInformation("Integrations: {Sent} sent, {Failed} failed", sent, failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Integration cycle failed"); }
            tick++;
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
