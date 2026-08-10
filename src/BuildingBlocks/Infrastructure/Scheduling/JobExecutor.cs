using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

/// <summary>Persists job runs (mt6_3). One per service, over its own DbContext.</summary>
public interface IJobRunStore
{
    public void Add(JobRun run);

    public Task SaveAsync(CancellationToken ct);

    public Task<List<JobRun>> RecentAsync(string? jobName, int take, CancellationToken ct);
}

/// <summary>EF-backed job-run store (mt6_3). Register one per service as <c>EfJobRunStore&lt;MyDbContext&gt;</c>.</summary>
public sealed class EfJobRunStore<TContext>(TContext db) : IJobRunStore
    where TContext : DbContext
{
    public void Add(JobRun run) => db.Set<JobRun>().Add(run);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public Task<List<JobRun>> RecentAsync(string? jobName, int take, CancellationToken ct) =>
        db.Set<JobRun>().AsNoTracking()
            .Where(r => jobName == null || r.JobName == jobName)
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .ToListAsync(ct);
}

/// <summary>
/// Runs a scheduled job and records its <see cref="JobRun"/> (mt6_3). A job failure is captured (status
/// Failed + error) and NOT rethrown, so one bad run never tears down the scheduler — the next cron tick
/// (or a retry) tries again.
/// </summary>
public sealed class JobExecutor(
    IJobRunStore store,
    TimeProvider clock,
    ILogger<JobExecutor> logger,
    MassTransit.IPublishEndpoint? publisher = null,
    Microsoft.Extensions.Options.IOptions<QuartzSchedulerOptions>? schedulerOptions = null,
    SchedulerIdentity? identity = null)
{
    public async Task<JobRun> ExecuteAsync(IScheduledJob job, CancellationToken ct)
    {
        var run = JobRun.Start(job.Name, clock.GetUtcNow());
        store.Add(run);
        await store.SaveAsync(ct); // mark Running before the work starts

        // Bounded retry (mt6_3): a transient failure is retried up to MaxJobRetries times before the run is
        // recorded Failed, so a blip doesn't wait a whole cron cycle to recover. 0 retries = original behaviour.
        var maxRetries = Math.Max(0, schedulerOptions?.Value.MaxJobRetries ?? 0);
        var retryDelay = TimeSpan.FromSeconds(Math.Max(0, schedulerOptions?.Value.RetryDelaySeconds ?? 0));
        Exception? lastError = null;
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                await job.ExecuteAsync(ct);
                lastError = null;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogWarning(ex, "Scheduled job {JobName} attempt {Attempt}/{Total} failed", job.Name, attempt + 1, maxRetries + 1);
                if (attempt < maxRetries && retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(retryDelay, ct);
                }
            }
        }

        if (lastError is null)
        {
            run.Succeed(clock.GetUtcNow());
        }
        else
        {
            run.Fail(clock.GetUtcNow(), lastError.Message);
            logger.LogError(lastError, "Scheduled job {JobName} failed after {Attempts} attempt(s)", job.Name, maxRetries + 1);
        }

        await store.SaveAsync(ct);

        // Project to the central Workflow service when a publisher is wired (tests run without one).
        if (publisher is not null)
        {
            await publisher.Publish(new JobRunRecorded(run.Id, run.JobName, run.Status.ToString(), run.StartedAt, run.CompletedAt, run.Error, identity?.ServiceName), ct);
        }

        return run;
    }
}
