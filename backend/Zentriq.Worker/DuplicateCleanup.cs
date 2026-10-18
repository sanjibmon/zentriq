using System.Text.Json;
using Npgsql;
using Zentriq.Infrastructure.Audit;
using Zentriq.Infrastructure.Inventory;

namespace Zentriq.Worker;

/// <summary>
/// Merges duplicate device records automatically (setting "inventory.duplicates" → auto, on by default): shortly after
/// start and every 30 minutes, so records created by a scan and by an agent for the same device become one.
/// Each merge is written to the audit log.
/// </summary>
public sealed class DuplicateCleanup(NpgsqlDataSource ds, AuditWriter audit, ILogger<DuplicateCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(ds, audit, logger, stoppingToken); await RefreshNetworkAsync(ds, stoppingToken); }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or JsonException)
            {
                logger.LogWarning("Duplicate clean-up failed: {Message}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    public static async Task<int> RunOnceAsync(NpgsqlDataSource ds, AuditWriter audit, ILogger logger, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using (var s = new NpgsqlCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'inventory.duplicates'", conn))
        {
            var json = await s.ExecuteScalarAsync(ct) as string;
            if (json is not null && JsonDocument.Parse(json).RootElement.TryGetProperty("auto", out var a) && a.ValueKind == JsonValueKind.False) return 0;
        }
        var merged = 0;
        foreach (var g in await DuplicateAssets.FindAsync(conn, ct))
        {
            try
            {
                merged += await DuplicateAssets.MergeAsync(conn, g.Keep.Id, g.Merge.Select(m => m.Id).ToList(), ct);
                await audit.WriteAsync(AuditActor.System("duplicates"), "asset.merge", g.Keep.Hostname ?? g.Keep.Ip,
                    detail: new { kept = g.Keep.Id, merged = g.Merge.Select(m => m.Id), reasons = g.Reasons, automatic = true }, ct: ct);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                logger.LogWarning("Could not merge duplicates of {Device}: {Message}", g.Keep.Hostname ?? g.Keep.Ip, ex.Message);
            }
        }
        if (merged > 0) logger.LogInformation("Merged {Count} duplicate device record(s).", merged);
        return merged;
    }

    /// <summary>Links devices found since the last network scan to the switch ports they are connected to.</summary>
    public static async Task RefreshNetworkAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await NetworkStore.RefreshAsync(conn, null, ct);
    }
}
