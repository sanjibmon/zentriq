using Npgsql;
using Zentriq.Infrastructure.Configuration;
using Zentriq.Infrastructure.Remote;

namespace Zentriq.Worker;

/// <summary>
/// Recorded remote sessions: every minute the recordings the remote support engine finished are added to the index
/// (Remote Support › History › Play), and recordings older than the retention period (90 days by default) are deleted.
/// </summary>
public sealed class RecordingService(NpgsqlDataSource ds, ZentriqPaths paths, RemoteSupportService remote, ILogger<RecordingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await remote.SettingsAsync(stoppingToken);
                var r = await RemoteRecordings.IndexAsync(ds, paths.RemoteRecordings, settings.RecordingRetentionDays, stoppingToken);
                if (r.Added > 0 || r.Deleted > 0) logger.LogInformation("Remote session recordings: {Added} added, {Deleted} deleted (older than {Days} days).", r.Added, r.Deleted, settings.RecordingRetentionDays);
            }
            catch (Exception ex) when (ex is NpgsqlException or IOException or UnauthorizedAccessException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Remote session recordings not indexed: {Message}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
