using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;
using ThreeCommerce.Payments.Infrastructure.Consumers;

namespace ThreeCommerce.Payments.Infrastructure.Scheduling;

/// <summary>
/// Retries past-due usage charges on the renewal dunning schedule (owner decision 2026-10-27,
/// <c>usage_charge_failure_feedback</c>): days 1, 3 and 7 after the first failure, a fresh provider key and the same pinned
/// amount each time, abandoning the charge when the day-7 retry does not settle. Hourly; each charge is retried at most
/// once per scheduled day (<see cref="Domain.DunningSchedule"/>), so the cadence only bounds how soon after its day a retry
/// runs. Idempotent; appears in the Scheduled-Job Manager. Gated by Scheduling:Enabled.
/// </summary>
public sealed class UsageChargeRetryJob(UsageChargeBilling billing) : IScheduledJob
{
    public string Name => "usage-charge-retry";

    public string CronSchedule => "0 30 * * * ?";

    public Task ExecuteAsync(CancellationToken ct) => billing.RetryDueAsync(ct);
}
