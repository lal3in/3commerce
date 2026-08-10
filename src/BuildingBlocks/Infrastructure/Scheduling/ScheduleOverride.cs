using Microsoft.EntityFrameworkCore;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

/// <summary>
/// An operator override of a scheduled job's code-default schedule (the Scheduled-Job Manager). Persisted per
/// service so a run-time change (a new cron, or pausing) survives a restart without requiring the Quartz
/// persistent store: <see cref="ScheduledJobRegistry"/> re-applies overrides on startup (override cron beats
/// the code default; <see cref="Paused"/> starts the trigger paused). One row per job name.
/// </summary>
public sealed class ScheduleOverride
{
    public required string JobName { get; init; }

    /// <summary>Operator cron, or null to use the job's code-default <see cref="IScheduledJob.CronSchedule"/>.</summary>
    public string? Cron { get; set; }
    public bool Paused { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Per-service persistence for <see cref="ScheduleOverride"/> (mirrors <see cref="IJobRunStore"/>).</summary>
public interface IScheduleOverrideStore
{
    public Task<List<ScheduleOverride>> AllAsync(CancellationToken ct);

    public Task<ScheduleOverride?> GetAsync(string jobName, CancellationToken ct);

    /// <summary>Upsert the override for a job and persist immediately.</summary>
    public Task UpsertAsync(string jobName, string? cron, bool paused, DateTimeOffset now, CancellationToken ct);
}

/// <summary>EF-backed override store. Register one per service as <c>EfScheduleOverrideStore&lt;MyDbContext&gt;</c>.</summary>
public sealed class EfScheduleOverrideStore<TContext>(TContext db) : IScheduleOverrideStore
    where TContext : DbContext
{
    public Task<List<ScheduleOverride>> AllAsync(CancellationToken ct) =>
        db.Set<ScheduleOverride>().AsNoTracking().ToListAsync(ct);

    public Task<ScheduleOverride?> GetAsync(string jobName, CancellationToken ct) =>
        db.Set<ScheduleOverride>().AsNoTracking().SingleOrDefaultAsync(x => x.JobName == jobName, ct);

    public async Task UpsertAsync(string jobName, string? cron, bool paused, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.Set<ScheduleOverride>().SingleOrDefaultAsync(x => x.JobName == jobName, ct);
        if (existing is null)
        {
            db.Set<ScheduleOverride>().Add(new ScheduleOverride { JobName = jobName, Cron = cron, Paused = paused, UpdatedAt = now });
        }
        else
        {
            existing.Cron = cron;
            existing.Paused = paused;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
