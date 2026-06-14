using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

public sealed class OrderConfirmedConsumer(IEmailSender sender, EmailTemplates templates, IOrderEmailLookup lookup) : IConsumer<OrderConfirmed>
{
    public async Task Consume(ConsumeContext<OrderConfirmed> context)
    {
        var m = context.Message;
        lookup.Remember(m.OrderId, m.Email);
        await sender.SendAsync(templates.OrderConfirmed(m.Email, m.OrderId, m.AmountMinor, m.Currency), context.CancellationToken);
    }
}
