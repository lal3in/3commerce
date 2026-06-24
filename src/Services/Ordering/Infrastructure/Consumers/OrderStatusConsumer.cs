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
        if (order is not null)
        {
            if (order.Status is not (OrderStatus.Pending or OrderStatus.AwaitingPayment))
            {
                return; // idempotent: already confirmed/cancelled
            }

            order.Status = OrderStatus.Confirmed;
            await db.SaveChangesAsync(context.CancellationToken);
        }
        else
        {
            var attempt = await db.CheckoutAttempts.Include(a => a.Lines)
                .SingleOrDefaultAsync(a => a.Id == context.Message.OrderId, context.CancellationToken);
            if (attempt is null || attempt.Status != CheckoutAttemptStatus.AwaitingPayment)
            {
                return;
            }

            var sequence = await db.OrderNumberSequences.SingleOrDefaultAsync(s => s.StorefrontId == attempt.StorefrontId, context.CancellationToken);
            if (sequence is null)
            {
                sequence = new OrderNumberSequence { StorefrontId = attempt.StorefrontId };
                db.OrderNumberSequences.Add(sequence);
            }

            order = attempt.ToOrder(sequence.ReserveNext(), DateTimeOffset.UtcNow);
            attempt.Status = CheckoutAttemptStatus.Confirmed;
            db.Orders.Add(order);
            await db.SaveChangesAsync(context.CancellationToken);
        }

        await context.Publish(new OrderConfirmed(
            order.Id, order.Email, order.GrossMinor, order.Currency,
            order.Lines.Select(l => new OrderLineInfo(
                l.ProductId, l.Title, l.Quantity, l.FulfilmentType, l.BillingMode, l.UnitPriceMinor)).ToList()));
    }

    public async Task Consume(ConsumeContext<OrderCancelled> context)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == context.Message.OrderId, context.CancellationToken);
        var attempt = await db.CheckoutAttempts.SingleOrDefaultAsync(a => a.Id == context.Message.OrderId, context.CancellationToken);
        if (order is not null && order.Status is OrderStatus.Pending or OrderStatus.AwaitingPayment)
        {
            order.Status = OrderStatus.Cancelled;
        }

        if (attempt is not null && attempt.Status == CheckoutAttemptStatus.AwaitingPayment)
        {
            attempt.Status = CheckoutAttemptStatus.Cancelled;
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
