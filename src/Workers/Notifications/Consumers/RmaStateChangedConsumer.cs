using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Support;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

public sealed class RmaStateChangedConsumer(IEmailSender sender, EmailTemplates templates) : IConsumer<RmaStateChanged>
{
    public Task Consume(ConsumeContext<RmaStateChanged> context) =>
        sender.SendAsync(templates.RmaStateChanged(context.Message.Email, context.Message.RmaId, context.Message.State), context.CancellationToken);
}
