using Npgsql;
using Zentriq.Infrastructure.Patches;
using Zentriq.Infrastructure.Scripts;

namespace Zentriq.Worker;

/// <summary>
/// Patch management and scripts in the background, every minute:
/// automatic scans for missing updates (devices whose last scan is older than the scan interval, 300 per round),
/// automatic deployment policies whose maintenance window is open (once per window), scheduled scripts and
/// remediations that are due, and the deletion of script output after its retention period (daily).
/// </summary>
public sealed class PatchScriptWorker(NpgsqlDataSource ds, ILogger<PatchScriptWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        try { await ScriptStore.EnsureBuiltInsAsync(ds, stoppingToken); }
        catch (NpgsqlException ex) { logger.LogWarning("Ready-made scripts could not be added: {Message}", ex.Message); }
        var nextScans = DateTimeOffset.UtcNow;
        var nextPurge = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextScans)
                {
                    var settings = await PatchStore.SettingsAsync(ds, stoppingToken);
                    if (settings.AutoScan)
                    {
                        var n = await PatchStore.QueueDueScansAsync(ds, settings.ScanHours, 300, stoppingToken);
                        if (n > 0) logger.LogInformation("Patch management: {Count} device(s) asked to scan for missing updates.", n);
                    }
                    nextScans = DateTimeOffset.UtcNow.AddMinutes(5);
                }
                foreach (var (policy, res) in await PatchStore.RunDuePoliciesAsync(ds, DateTime.Now, stoppingToken))
                    logger.LogInformation("Patch policy {Policy}: updates sent to {Sent} device(s), {Skipped} skipped ({Nothing} up to date).", policy.Name, res.Sent, res.Skipped, res.Nothing);
                var ran = await ScriptStore.RunDueSchedulesAsync(ds, stoppingToken);
                if (ran > 0) logger.LogInformation("Scripts: {Count} scheduled run(s) started.", ran);
                if (DateTimeOffset.UtcNow >= nextPurge)
                {
                    var s = await ScriptStore.SettingsAsync(ds, stoppingToken);
                    var purged = await ScriptStore.PurgeOutputAsync(ds, s.KeepOutputDays, stoppingToken);
                    if (purged > 0) logger.LogInformation("Scripts: output of {Count} run(s) older than {Days} days deleted.", purged, s.KeepOutputDays);
                    nextPurge = DateTimeOffset.UtcNow.AddHours(24);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Patch management / scripts: {Message}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
