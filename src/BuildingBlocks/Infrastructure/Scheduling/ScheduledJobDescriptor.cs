namespace ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

/// <summary>
/// The current schedule/state of a scheduled job (Scheduled-Job Manager). Published by each service at startup
/// and after any run-time change (pause/resume/reschedule) so the central Workflow registry — rendered in
/// Mission Control — can show a live cross-service job list without querying each service directly (ADR-0008).
/// </summary>
public record ScheduledJobDescriptor(
    string Service,
    string Name,
    string Cron,
    bool Paused,
    DateTimeOffset? NextFireUtc);

/// <summary>The owning service's stable key (e.g. "payments"), stamped on <see cref="JobRunRecorded"/> and
/// <see cref="ScheduledJobDescriptor"/> so the manager knows which service owns — and can route control to —
/// each job. Registered as a singleton by <c>AddScheduledJobs</c>.</summary>
public sealed record SchedulerIdentity(string ServiceName);

/// <summary>The jobs registered in this host (name + code-default cron), exposed so the startup registrar and
/// the control endpoints can enumerate them. Registered as a singleton by <c>AddScheduledJobs</c>.</summary>
public sealed class RegisteredScheduledJobs
{
    public required IReadOnlyList<(string Name, string DefaultCron)> Jobs { get; init; }
}
