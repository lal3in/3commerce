using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

/// <summary>
/// Emails the customer when a metered usage charge was declined (and will be retried) or abandoned after its final retry
/// (owner decision 2026-10-27, usage_charge_failure_feedback — the same rules as subscription dunning). The Usage service
/// consumes the same event to reverse the credit, so this consumer is named for the email (ADR-0060).
/// </summary>
public sealed class UsageChargeFailedEmailConsumer(IEmailSender sender, EmailTemplates templates) : IConsumer<UsageChargeFailed>
{
    public Task Consume(ConsumeContext<UsageChargeFailed> context) =>
        sender.SendAsync(templates.UsageChargeFailed(context.Message), context.CancellationToken);
}
