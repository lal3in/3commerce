namespace ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

/// <summary>
/// A typed recurring job (mt6_3). Implementations declare their cron and do the work; the scheduler
/// (Quartz) fires <see cref="ExecuteAsync"/> on the cron and the executor records a <see cref="JobRun"/>.
/// GOTCHA: jobs are typed code — never admin-defined SQL/shell.
/// </summary>
public interface IScheduledJob
{
    /// <summary>Stable identifier (also the Quartz job key and the JobRun.JobName).</summary>
    public string Name { get; }

    /// <summary>Quartz cron expression, e.g. "0 0 2 * * ?" (daily 02:00).</summary>
    public string CronSchedule { get; }

    public Task ExecuteAsync(CancellationToken ct);
}

public enum JobRunStatus { Running = 1, Succeeded = 2, Failed = 3 }

/// <summary>A record of one execution of a scheduled job (mt6_3) — the audit trail for the scheduler.</summary>
public sealed class JobRun
{
    public Guid Id { get; init; }
    public required string JobName { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public JobRunStatus Status { get; private set; } = JobRunStatus.Running;
    public string? Error { get; private set; }

    private JobRun() { }

    public static JobRun Start(string jobName, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(), JobName = jobName, StartedAt = now };

    public void Succeed(DateTimeOffset now)
    {
        Status = JobRunStatus.Succeeded;
        CompletedAt = now;
    }

    public void Fail(DateTimeOffset now, string error)
    {
        Status = JobRunStatus.Failed;
        CompletedAt = now;
        Error = error.Length > 1000 ? error[..1000] : error;
    }
}
