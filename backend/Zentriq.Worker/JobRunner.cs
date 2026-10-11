using Zentriq.Infrastructure.Jobs;

namespace Zentriq.Worker;

public sealed record WorkerIdentity(string Id);

public sealed class JobContext(LeasedJob job, JobQueue queue)
{
    public LeasedJob Job { get; } = job;
    public Task ProgressAsync(int pct, string? text, CancellationToken ct = default) => queue.ReportProgressAsync(Job.Id, pct, text, ct);
    public Task LogAsync(string message, string level = "info", CancellationToken ct = default) => queue.LogAsync(Job.Id, level, message, ct);
}

public interface IJobHandler
{
    string JobType { get; }
    Task<object?> ExecuteAsync(JobContext context, CancellationToken ct);
}

/// <summary>Thrown for failures that will not succeed on retry (bad input, missing records).</summary>
public sealed class PermanentJobException(string message) : Exception(message);

/// <summary>
/// Runs durable jobs with bounded parallelism. For each job: lease → execute with a linked token
/// (service stop, job timeout, user cancellation polled through lease renewal) → complete / fail /
/// cancel. On service stop running jobs are released back to the queue without using an attempt.
/// </summary>
public sealed class JobRunner(IServiceScopeFactory scopes, JobQueue queue, WorkerIdentity identity, ILogger<JobRunner> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    private const int Slots = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Job runner {Worker} started with {Slots} slots", identity.Id, Slots);
        var running = new List<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);
            LeasedJob? job = null;
            if (running.Count < Slots)
            {
                try { job = await queue.LeaseNextAsync(identity.Id, Lease, maintenance: false, stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "Could not lease a job (database unavailable?)"); }
            }
            if (job is not null) { running.Add(RunAsync(job, stoppingToken)); continue; }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch (OperationCanceledException) { break; }
        }
        await Task.WhenAll(running);
        logger.LogInformation("Job runner stopped");
    }

    private async Task RunAsync(LeasedJob job, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.JobType == job.JobType);
        if (handler is null)
        {
            await queue.FailAsync(job.Id, identity.Id, $"No handler for job type '{job.JobType}'.", retryable: false);
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(job.TimeoutSeconds));
        using var userCancel = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token, userCancel.Token);
        var ctx = new JobContext(job, queue);

        var renew = Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), linked.Token);
                    if (await queue.RenewLeaseAsync(job.Id, identity.Id, Lease)) userCancel.Cancel();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "Lease renewal failed for {Job}", job.Id); }
            }
        });

        try
        {
            logger.LogInformation("Running {Type} job {Job} (attempt {Attempt}/{Max})", job.JobType, job.Id, job.Attempts, job.MaxAttempts);
            var result = await handler.ExecuteAsync(ctx, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            await queue.CompleteAsync(job.Id, identity.Id, result, CancellationToken.None);
            logger.LogInformation("Job {Job} succeeded", job.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await queue.ReleaseAsync(job.Id, identity.Id, CancellationToken.None);
            logger.LogInformation("Job {Job} released for another worker because the service is stopping", job.Id);
        }
        catch (OperationCanceledException) when (userCancel.IsCancellationRequested)
        {
            await ctx.LogAsync("Cancelled by user.", "warning", CancellationToken.None);
            await queue.MarkCancelledAsync(job.Id, identity.Id, CancellationToken.None);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await ctx.LogAsync($"Timed out after {job.TimeoutSeconds} s.", "error", CancellationToken.None);
            await queue.FailAsync(job.Id, identity.Id, $"Timed out after {job.TimeoutSeconds} seconds.", retryable: true, CancellationToken.None);
        }
        catch (PermanentJobException ex)
        {
            await ctx.LogAsync(ex.Message, "error", CancellationToken.None);
            await queue.FailAsync(job.Id, identity.Id, ex.Message, retryable: false, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {Job} failed", job.Id);
            await ctx.LogAsync("Attempt failed: " + ex.Message, "error", CancellationToken.None);
            await queue.FailAsync(job.Id, identity.Id, ex.Message, retryable: true, CancellationToken.None);
        }
        finally
        {
            linked.Cancel();
            try { await renew; } catch { }
        }
    }
}
