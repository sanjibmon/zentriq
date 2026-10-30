using Zentriq.Infrastructure.Licensing;

namespace Zentriq.Worker;

/// <summary>
/// Online licenses: checks in with the Zentriq license server once a day (hourly while the lease ends within 10 days) —
/// reports usage and picks up renewals, upgrades, support extensions, a new lease, or the end of the activation.
/// </summary>
public sealed class LicenseCheckin(IServiceScopeFactory scopes, ILogger<LicenseCheckin> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var online = scope.ServiceProvider.GetRequiredService<OnlineLicensing>();
                var licensing = scope.ServiceProvider.GetRequiredService<LicensingService>();
                var st = await online.LoadStateAsync(stoppingToken);
                if (st.Active)
                {
                    var status = await licensing.GetStatusAsync(stoppingToken);
                    if (OnlineLicensing.CheckinDue(st, status.LeaseUntil, DateTimeOffset.UtcNow))
                    {
                        var r = await online.CheckinAsync("scheduled", stoppingToken);
                        if (r.Ok) logger.LogInformation("License check-in: {Message}", r.Message);
                        else logger.LogWarning("License check-in failed: {Message}", r.Message);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "License check-in cycle failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
