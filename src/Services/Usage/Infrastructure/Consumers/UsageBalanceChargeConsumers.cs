using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;

namespace ThreeCommerce.Usage.Infrastructure.Consumers;

/// <summary>
/// Payments settled an overage / auto-load charge (ADR-0063 P4b): the balance's charge is paid, a credit taken back after
/// an earlier decline is re-granted, and the billing hold is released when nothing else is unpaid
/// (usage_charge_failure_feedback). Named for the Usage balance it updates — <see cref="UsageChargeSettled"/> is also an
/// invoicing input, so its consumers must not share a queue (ADR-0060).
/// </summary>
public sealed class UsageBalanceChargeSettledConsumer(UsageService usage) : IConsumer<UsageChargeSettled>
{
    public Task Consume(ConsumeContext<UsageChargeSettled> context) => usage.ApplySettledAsync(context.Message, context.CancellationToken);
}

/// <summary>
/// Payments could not settle an overage / auto-load charge (owner decision 2026-10-27, usage_charge_failure_feedback): the
/// auto-load's optimistic credit is taken back on the first failure and the balance's billing is held (Unpaid while
/// Payments retries on days 1, 3 and 7; Suspended once the final retry failed). Unique name (ADR-0060) — Notifications
/// consumes the same event for the customer email.
/// </summary>
public sealed class UsageBalanceChargeFailedConsumer(UsageService usage) : IConsumer<UsageChargeFailed>
{
    public Task Consume(ConsumeContext<UsageChargeFailed> context) => usage.ApplyFailedAsync(context.Message, context.CancellationToken);
}
