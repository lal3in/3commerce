using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Consumers;

/// <summary>Applies the saga's terminal decision to the Order aggregate (status projection).</summary>
public sealed class OrderStatusConsumer(OrderingDbContext db) :
    IConsumer<OrderConfirmed>, IConsumer<OrderCancelled>
{
    public async Task Consume(ConsumeContext<OrderConfirmed> context) =>
        await SetStatus(context.Message.OrderId, OrderStatus.Confirmed, context.CancellationToken);

    public async Task Consume(ConsumeContext<OrderCancelled> context) =>
        await SetStatus(context.Message.OrderId, OrderStatus.Cancelled, context.CancellationToken);

    private async Task SetStatus(Guid orderId, OrderStatus status, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is not null && order.Status is OrderStatus.Pending or OrderStatus.AwaitingPayment)
        {
            order.Status = status;
            await db.SaveChangesAsync(ct);
        }
    }
}
