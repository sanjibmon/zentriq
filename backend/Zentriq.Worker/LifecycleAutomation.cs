using Npgsql;
using Zentriq.Infrastructure.Lifecycle;

namespace Zentriq.Worker;

/// <summary>
/// Asset Lifecycle housekeeping: every hour, IT assets in use that Zentriq has not seen for the configured number of days
/// (setting lifecycle.not_seen_days, default 60; 0 = off) become Missing · Not seen. Seeing them again brings them back
/// (database trigger). Warranty, contract and loan reminders are raised by the integration detectors.
/// </summary>
public sealed class LifecycleAutomation(NpgsqlDataSource ds, ILogger<LifecycleAutomation> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await ds.OpenConnectionAsync(stoppingToken);
                var n = await LifecycleStore.MarkNotSeenAsync(conn, stoppingToken);
                if (n > 0) logger.LogInformation("Asset Lifecycle: {Count} IT asset(s) not seen recently marked Missing · Not seen.", n);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                logger.LogWarning("Asset Lifecycle housekeeping failed: {Message}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
