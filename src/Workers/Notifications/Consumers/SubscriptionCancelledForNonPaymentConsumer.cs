using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

/// <summary>
/// Emails the customer when their subscription was cancelled because the final scheduled retry of an unpaid renewal did
/// not settle (dunning, ADR-0057 amendment 2026-10-27). The only consumer of
/// <see cref="SubscriptionCancelledForNonPayment"/> (ADR-0060).
/// </summary>
public sealed class SubscriptionCancelledForNonPaymentConsumer(IEmailSender sender, EmailTemplates templates)
    : IConsumer<SubscriptionCancelledForNonPayment>
{
    public Task Consume(ConsumeContext<SubscriptionCancelledForNonPayment> context) =>
        sender.SendAsync(templates.SubscriptionCancelledForNonPayment(context.Message), context.CancellationToken);
}
