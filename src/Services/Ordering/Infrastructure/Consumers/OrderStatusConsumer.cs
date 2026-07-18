using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Consumers;

/// <summary>
/// Applies the saga's terminal decision to the Order aggregate and, on success, publishes
/// the rich OrderConfirmed (with line items) — sourced from the aggregate, so downstream
/// services (Fulfillment, Notifications) never query Ordering directly (ADR-0008).
/// </summary>
public sealed class OrderStatusConsumer(OrderingDbContext db) :
    IConsumer<CheckoutCompleted>, IConsumer<OrderCancelled>, IConsumer<RefundCompleted>
{
    /// <summary>
    /// A fully-refunded order moves Confirmed → Refunded so the admin order list stops offering
    /// "Refund" and shows the true state. Partial refunds leave the order Confirmed (the money moved
    /// but the order still stands). Idempotent: only a Confirmed order transitions.
    /// </summary>
    public async Task Consume(ConsumeContext<RefundCompleted> context)
    {
        if (!context.Message.FullyRefunded)
        {
            return;
        }

        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == context.Message.OrderId, context.CancellationToken);
        if (order is not null && order.Status == OrderStatus.Confirmed)
        {
            order.Status = OrderStatus.Refunded;
            await db.SaveChangesAsync(context.CancellationToken);
        }
    }

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
            await AttachVerifiedOwnerAsync(order, context.CancellationToken);
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
            await AttachVerifiedOwnerAsync(order, context.CancellationToken);
            db.Orders.Add(order);
            await db.SaveChangesAsync(context.CancellationToken);
        }

        await context.Publish(new OrderConfirmed(
            order.Id, order.TenantId, order.Email, order.GrossMinor, order.Currency,
            new ShipToInfo(order.ShipName, order.ShipLine1, order.ShipCity, order.ShipPostcode, order.ShipCountry, order.ShipRegion),
            order.Lines.Select(l => new OrderLineInfo(
                l.ProductId, l.VariantId, l.SupplierId, l.Title, l.Quantity, l.FulfilmentType, l.BillingMode, l.UnitPriceMinor)).ToList()));

        // Recurring lines set up a subscription in Payments (mt7_3); the first period was paid with the order.
        foreach (var line in order.Lines.Where(l => l.BillingMode == BillingMode.Recurring && l.BillingPeriod != BillingPeriod.Once))
        {
            await context.Publish(new SubscriptionRequested(
                order.TenantId, order.Id, order.Email, line.ProductId, line.VariantId, line.BillingPeriod, line.UnitPriceMinor, order.Currency));
        }
    }

    /// <summary>
    /// FR-7 (both directions): a guest order confirming AFTER the shopper already verified an
    /// account with that email attaches at creation — the EmailVerified sweep in
    /// <see cref="GuestOrderAttachConsumer"/> only catches orders that existed at that moment.
    /// Only ever fills a missing owner; never overwrites an authenticated checkout's UserId,
    /// and only from a VERIFIED email (the copy row is written on EmailVerified alone).
    /// </summary>
    private async Task AttachVerifiedOwnerAsync(Order order, CancellationToken ct)
    {
        if (order.UserId is not null)
        {
            return;
        }

        var email = order.Email.Trim().ToLowerInvariant();
        var verified = await db.VerifiedCustomerCopies.SingleOrDefaultAsync(c => c.Email == email, ct);
        if (verified is not null)
        {
            order.UserId = verified.UserId;
        }
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
