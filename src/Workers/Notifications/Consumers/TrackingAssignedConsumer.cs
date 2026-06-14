using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Workers.Notifications.Consumers;

public sealed class TrackingAssignedConsumer(IEmailSender sender, EmailTemplates templates, IOrderEmailLookup lookup)
    : IConsumer<TrackingAssigned>
{
    public async Task Consume(ConsumeContext<TrackingAssigned> context)
    {
        var m = context.Message;
        var to = await lookup.EmailForOrderAsync(m.OrderId) ?? "operations@3commerce.local";
        await sender.SendAsync(templates.TrackingAssigned(to, m.OrderId, m.Carrier, m.TrackingNumber), context.CancellationToken);
    }
}
