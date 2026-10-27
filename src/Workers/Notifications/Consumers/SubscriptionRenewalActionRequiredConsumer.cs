using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

/// <summary>
/// Emails the customer when a subscription renewal needs them to authenticate the payment (ADR-0057 amendment
/// 2026-10-27). The only consumer of <see cref="SubscriptionRenewalActionRequired"/>; the class name is unique across
/// services, so its queue is its own (ADR-0060).
/// </summary>
public sealed class SubscriptionRenewalActionRequiredConsumer(IEmailSender sender, EmailTemplates templates)
    : IConsumer<SubscriptionRenewalActionRequired>
{
    public Task Consume(ConsumeContext<SubscriptionRenewalActionRequired> context) =>
        sender.SendAsync(templates.SubscriptionRenewalActionRequired(context.Message), context.CancellationToken);
}
