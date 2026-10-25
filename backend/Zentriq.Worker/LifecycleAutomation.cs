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
                var tags = await TagSettings.LoadAsync(conn, null, stoppingToken);
                if (tags.Auto && tags.TagDiscovered)
                {
                    var ids = new List<Guid>();
                    await using (var q = new NpgsqlCommand("""
                        SELECT l.asset_id FROM zentriq.asset_lifecycle l JOIN zentriq.assets a ON a.id = l.asset_id
                         WHERE l.asset_tag IS NULL AND a.status = 'active' AND l.stage <> 'disposed' ORDER BY a.first_seen_at LIMIT 2000
                        """, conn))
                    await using (var r = await q.ExecuteReaderAsync(stoppingToken))
                        while (await r.ReadAsync(stoppingToken)) ids.Add(r.GetGuid(0));
                    if (ids.Count > 0)
                    {
                        await using var tx = await conn.BeginTransactionAsync(stoppingToken);
                        foreach (var id in ids) await LifecycleStore.EnsureTagAsync(conn, tx, id, stoppingToken, tags);
                        await tx.CommitAsync(stoppingToken);
                        logger.LogInformation("Asset Lifecycle: {Count} new IT asset(s) given an asset tag.", ids.Count);
                    }
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or LifecycleValidationException)
            {
                logger.LogWarning("Asset Lifecycle housekeeping failed: {Message}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
