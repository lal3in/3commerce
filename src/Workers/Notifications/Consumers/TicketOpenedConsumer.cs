using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Support;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

public sealed class TicketOpenedConsumer(IEmailSender sender, EmailTemplates templates) : IConsumer<TicketOpened>
{
    public Task Consume(ConsumeContext<TicketOpened> context) =>
        sender.SendAsync(templates.TicketOpened(context.Message.Email, context.Message.TicketId, context.Message.OrderId), context.CancellationToken);
}
