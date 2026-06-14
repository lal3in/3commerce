using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Consumers;

/// <summary>
/// Applies the saga's terminal decision to the Order aggregate and, on success, publishes
/// the rich OrderConfirmed (with line items) — sourced from the aggregate, so downstream
/// services (Fulfillment, Notifications) never query Ordering directly (ADR-0008).
/// </summary>
public sealed class OrderStatusConsumer(OrderingDbContext db) :
    IConsumer<CheckoutCompleted>, IConsumer<OrderCancelled>
{
    public async Task Consume(ConsumeContext<CheckoutCompleted> context)
    {
        var order = await db.Orders.Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Id == context.Message.OrderId, context.CancellationToken);
        if (order is null || order.Status is not (OrderStatus.Pending or OrderStatus.AwaitingPayment))
        {
            return; // idempotent: already confirmed/cancelled
        }

        order.Status = OrderStatus.Confirmed;
        await db.SaveChangesAsync(context.CancellationToken);

        await context.Publish(new OrderConfirmed(
            order.Id, order.Email, order.GrossMinor, order.Currency,
            order.Lines.Select(l => new OrderLineInfo(
                l.ProductId, l.Title, l.Quantity, l.FulfillmentSource.ToString())).ToList()));
    }

    public async Task Consume(ConsumeContext<OrderCancelled> context)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == context.Message.OrderId, context.CancellationToken);
        if (order is not null && order.Status is OrderStatus.Pending or OrderStatus.AwaitingPayment)
        {
            order.Status = OrderStatus.Cancelled;
            await db.SaveChangesAsync(context.CancellationToken);
        }
    }
}
