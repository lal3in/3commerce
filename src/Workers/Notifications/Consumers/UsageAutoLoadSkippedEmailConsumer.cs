using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

/// <summary>
/// Emails the customer to add a card when an automatic top-up was skipped because they have no default saved card (owner
/// decision 2026-11-06, jobmgr_3_settlement). The Usage service consumes the same event to take back the credit, so this
/// consumer is named for the email (ADR-0060).
/// </summary>
public sealed class UsageAutoLoadSkippedEmailConsumer(IEmailSender sender, EmailTemplates templates) : IConsumer<UsageAutoLoadSkipped>
{
    public Task Consume(ConsumeContext<UsageAutoLoadSkipped> context) =>
        sender.SendAsync(templates.UsageAutoLoadSkipped(context.Message), context.CancellationToken);
}
