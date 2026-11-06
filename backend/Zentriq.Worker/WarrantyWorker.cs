using Npgsql;
using Zentriq.Infrastructure.Lifecycle.Warranty;

namespace Zentriq.Worker;

/// <summary>
/// Online warranty lookup in the background: every 30 minutes up to 200 Dell, Lenovo and HP devices that are new or due
/// (found warranties again after the refresh period, 30 days by default) are looked up at the manufacturer;
/// <i>Look Up All</i> in the console asks for a run within a minute.
/// </summary>
public sealed class WarrantyWorker(NpgsqlDataSource ds, WarrantyService warranty, ILogger<WarrantyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }
        var next = DateTimeOffset.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var requested = await RequestedAsync(stoppingToken);
                if (requested || DateTimeOffset.UtcNow >= next)
                {
                    var settings = await warranty.SettingsAsync(stoppingToken);
                    if (settings.Auto || requested)
                    {
                        // A requested run goes through every device that is due (in rounds of 200).
                        for (var round = 0; round < (requested ? 25 : 1); round++)
                        {
                            var r = await warranty.RunDueAsync(200, stoppingToken);
                            if (r.Checked + r.Errors > 0)
                                logger.LogInformation("Warranty lookup: {Checked} device(s) checked, {Found} found, {NotFound} not found, {Errors} failed{More}.",
                                    r.Checked, r.Found, r.NotFound, r.Errors, r.Messages.Count > 0 ? ": " + string.Join(" ", r.Messages) : "");
                            if (r.Checked + r.Errors + r.NoKey + r.Unsupported < 200 || r.Errors > 0) break;
                        }
                    }
                    next = DateTimeOffset.UtcNow.AddMinutes(30);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or System.Security.Cryptography.CryptographicException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Warranty lookup failed: {Message}", ex.Message);
                next = DateTimeOffset.UtcNow.AddMinutes(30);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> RequestedAsync(CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"DELETE FROM zentriq.system_settings WHERE key = '{WarrantyService.RequestedKey}' RETURNING 1");
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
