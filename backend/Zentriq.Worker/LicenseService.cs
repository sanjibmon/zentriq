using Npgsql;
using Zentriq.Infrastructure.Inventory;
using Zentriq.Infrastructure.Licenses;

namespace Zentriq.Worker;

/// <summary>
/// Software License Management, Web Apps &amp; Accounts and network vendors in the background:
/// <list type="bullet">
/// <item>every 15 minutes the licence position, expiry dates, Windows / Office activation and web app accounts are
/// evaluated; new alerts are announced (bell, e-mail digest, integration events);</item>
/// <item>every 6 hours each Microsoft 365 connection is read (licences, users, single sign-on apps);</item>
/// <item>every week the IEEE MAC address vendor registry is downloaded (when the server has internet access).</item>
/// </list>
/// </summary>
public sealed class LicenseService(NpgsqlDataSource ds, M365Sync m365, LicenseNotifier notifier, ILogger<LicenseService> logger) : BackgroundService
{
    private DateTimeOffset _nextVendors = DateTimeOffset.UtcNow.AddMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncCloudAsync(stoppingToken);
            try
            {
                var r = await LicenseEvaluator.RunAsync(ds, stoppingToken);
                if (r.Opened > 0 || r.Resolved > 0) logger.LogInformation("Software licences: {Open} open alert(s), {Opened} new, {Resolved} resolved.", r.Open, r.Opened, r.Resolved);
                var sent = await notifier.RunAsync(stoppingToken);
                if (sent.Alerts > 0) logger.LogInformation("Software licences: {Alerts} new alert(s) announced ({InApp} in the console, {Mailed} e-mail(s)).", sent.Alerts, sent.InApp, sent.Mailed);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Software licence evaluation failed: {Message}", ex.Message);
            }
            await VendorsAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task SyncCloudAsync(CancellationToken ct)
    {
        var due = new List<(Guid Id, string Name)>();
        try
        {
            await using var cmd = ds.CreateCommand("""
                SELECT id, name FROM zentriq.sam_cloud_connections
                 WHERE enabled AND (last_sync_at IS NULL OR last_sync_at < now() - interval '6 hours' OR (last_status = 'requested'))
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) due.Add((r.GetGuid(0), r.GetString(1)));
        }
        catch (NpgsqlException ex) { logger.LogWarning("Microsoft 365 connections not read: {Message}", ex.Message); return; }
        foreach (var (id, name) in due)
        {
            try
            {
                var o = await m365.RunAsync(id, ct);
                logger.LogInformation("Microsoft 365 '{Name}': {Skus} licence type(s), {Users} user(s), {Apps} single sign-on app(s) with {Accounts} account(s).", name, o.Skus, o.Users, o.Apps, o.Accounts);
            }
            catch (M365Exception ex) { logger.LogWarning("Microsoft 365 '{Name}' sync failed: {Message}", name, ex.Message); }
            catch (NpgsqlException ex) { logger.LogWarning("Microsoft 365 '{Name}' sync failed: {Message}", name, ex.Message); }
        }
    }

    private async Task VendorsAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _nextVendors) return;
        try
        {
            await using (var cmd = ds.CreateCommand($"SELECT (SELECT count(*) FROM zentriq.mac_vendors), (SELECT (value->>'updatedAt')::timestamptz FROM zentriq.system_settings WHERE key = '{MacVendors.StatusKey}')"))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                await r.ReadAsync(ct);
                var count = r.GetInt64(0);
                var updated = r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(1);
                if (count > 1000 && updated is { } u && u > DateTimeOffset.UtcNow.AddDays(-7)) { _nextVendors = u.AddDays(7); return; }
            }
            var n = await MacVendors.DownloadAsync(ds, ct);
            logger.LogInformation("MAC address vendors updated: {Count} prefixes from the IEEE registry.", n);
            _nextVendors = DateTimeOffset.UtcNow.AddDays(7);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogInformation("MAC address vendors not updated: {Message}", ex.Message);
            try { await MacVendors.RecordErrorAsync(ds, ex.Message, ct); } catch (NpgsqlException) { }
            _nextVendors = DateTimeOffset.UtcNow.AddDays(1);
        }
        catch (NpgsqlException ex)
        {
            logger.LogWarning("MAC address vendors not updated: {Message}", ex.Message);
            _nextVendors = DateTimeOffset.UtcNow.AddHours(1);
        }
    }
}
