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

/// <summary>
/// Payments skipped an auto top-up because the customer has no default saved card (owner decision 2026-11-06,
/// jobmgr_3_settlement): the top-up's credit is taken back and auto-load pauses until a card is reported. Unique name
/// (ADR-0060) — Notifications consumes the same event for the "add a card" email.
/// </summary>
public sealed class UsageBalanceAutoLoadSkippedConsumer(UsageService usage) : IConsumer<UsageAutoLoadSkipped>
{
    public Task Consume(ConsumeContext<UsageAutoLoadSkipped> context) => usage.ApplySkippedAsync(context.Message, context.CancellationToken);
}

/// <summary>
/// The customer has a default saved card again (owner decision 2026-11-06): auto-load paused for want of one resumes, and
/// a balance at its threshold tops up now. Named for the Usage balance it updates (ADR-0060).
/// </summary>
public sealed class UsageBalancePaymentMethodReadyConsumer(UsageService usage) : IConsumer<CustomerPaymentMethodReady>
{
    public Task Consume(ConsumeContext<CustomerPaymentMethodReady> context) =>
        usage.ApplyPaymentMethodReadyAsync(context.Message, context.CancellationToken);
}
