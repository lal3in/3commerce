using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

namespace ThreeCommerce.Marketing.Infrastructure;

/// <summary>
/// Applies due scheduled publishes (def_5 / mt5_7 + mt6_3): every minute, anything whose
/// scheduled time has arrived goes live. Each fire is recorded as a JobRun for the audit trail.
/// </summary>
public sealed class ScheduledPublishJob(PublishingService publishing) : IScheduledJob
{
    public string Name => "scheduled-publish";

    public string CronSchedule => "0 * * * * ?"; // every minute — publish latency ≤60s

    public Task ExecuteAsync(CancellationToken ct) => publishing.SweepDueScheduledAsync(ct);
}
