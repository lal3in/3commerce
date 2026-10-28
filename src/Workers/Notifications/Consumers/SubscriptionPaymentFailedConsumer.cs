using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

/// <summary>
/// Emails the customer when a subscription renewal was declined and will be retried, linking the "complete payment"
/// page (dunning, ADR-0057 amendment 2026-10-27). The only consumer of <see cref="SubscriptionPaymentFailed"/> (ADR-0060).
/// </summary>
public sealed class SubscriptionPaymentFailedConsumer(IEmailSender sender, EmailTemplates templates)
    : IConsumer<SubscriptionPaymentFailed>
{
    public Task Consume(ConsumeContext<SubscriptionPaymentFailed> context) =>
        sender.SendAsync(templates.SubscriptionPaymentFailed(context.Message), context.CancellationToken);
}
