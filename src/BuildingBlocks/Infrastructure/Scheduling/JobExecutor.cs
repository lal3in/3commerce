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
public sealed class JobExecutor(IJobRunStore store, TimeProvider clock, ILogger<JobExecutor> logger)
{
    public async Task<JobRun> ExecuteAsync(IScheduledJob job, CancellationToken ct)
    {
        var run = JobRun.Start(job.Name, clock.GetUtcNow());
        store.Add(run);
        await store.SaveAsync(ct); // mark Running before the work starts

        try
        {
            await job.ExecuteAsync(ct);
            run.Succeed(clock.GetUtcNow());
        }
        catch (Exception ex)
        {
            run.Fail(clock.GetUtcNow(), ex.Message);
            logger.LogError(ex, "Scheduled job {JobName} failed", job.Name);
        }

        await store.SaveAsync(ct);
        return run;
    }
}
