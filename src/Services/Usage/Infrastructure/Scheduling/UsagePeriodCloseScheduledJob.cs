using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

namespace ThreeCommerce.Usage.Infrastructure.Scheduling;

/// <summary>
/// Auto-closes metered usage periods that have ended (mt7_5 closing flow): bills any unbilled overage and
/// rolls each due balance to its next period. Runs hourly so a period is closed promptly after its window
/// ends; the sweep is idempotent, so a re-run before new usage is a no-op. The scheduler is gated by
/// Scheduling:Enabled (off in integration tests, which boot many hosts in one process).
/// </summary>
public sealed class UsagePeriodCloseScheduledJob(UsageService usage) : IScheduledJob
{
    public string Name => "usage-period-close";

    // Top of every hour — periods close within the hour after their window ends.
    public string CronSchedule => "0 0 * * * ?";

    public Task ExecuteAsync(CancellationToken ct) => usage.CloseDuePeriodsAsync(ct);
}
