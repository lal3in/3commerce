using System.Globalization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Reference;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure.Consumers;

/// <summary>
/// Applies the saga's terminal decision to the Order aggregate and, on success, publishes
/// the rich OrderConfirmed (with line items) — sourced from the aggregate, so downstream
/// services (Fulfillment, Notifications) never query Ordering directly (ADR-0008).
/// </summary>
public sealed class OrderStatusConsumer(OrderingDbContext db, IAuditRecorder audit, ILogger<OrderStatusConsumer> logger) :
    IConsumer<CheckoutCompleted>, IConsumer<OrderCancelled>, IConsumer<RefundCompleted>, IConsumer<PaymentDisputed>, IConsumer<PaymentChargedBack>
{
    /// <summary>
    /// A chargeback opened against the order's payment (phase 2): flag it Disputed so the admin and the
    /// shopper see the state. The order stays in its current status (the money already moved via the
    /// ledger reversal); this is a badge, like <see cref="Order.PartiallyRefunded"/>. Idempotent.
    /// </summary>
    public async Task Consume(ConsumeContext<PaymentDisputed> context)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == context.Message.OrderId, context.CancellationToken);
        if (order is null || order.Disputed)
        {
            return; // idempotent
        }

        order.Disputed = true;
        await db.SaveChangesAsync(context.CancellationToken);
    }

    /// <summary>
    /// The dispute closed as lost — the terminal chargeback outcome. Ensure the order carries the Disputed
    /// badge even if the earlier <see cref="PaymentDisputed"/> was never seen (a dispute can open and lose
    /// in a single delivery window). Idempotent; the money already moved via the ledger reversal.
    /// </summary>
    public async Task Consume(ConsumeContext<PaymentChargedBack> context)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == context.Message.OrderId, context.CancellationToken);
        if (order is null || order.Disputed)
        {
            return; // idempotent
        }

        order.Disputed = true;
        await db.SaveChangesAsync(context.CancellationToken);
    }

    /// <summary>
    /// A fully-refunded order moves Confirmed → Refunded so the admin order list stops offering
    /// "Refund" and shows the true state. Partial refunds leave the order Confirmed (the money moved
    /// but the order still stands). Idempotent: only a Confirmed order transitions.
    /// </summary>
    public async Task Consume(ConsumeContext<RefundCompleted> context)
    {
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == context.Message.OrderId, context.CancellationToken);
        if (order is null || order.Status != OrderStatus.Confirmed)
        {
            return; // idempotent: only a Confirmed order transitions
        }

        if (context.Message.FullyRefunded)
        {
            order.Status = OrderStatus.Refunded;
        }
        else
        {
            // Partial refund: the order still stands (stays Confirmed) but is flagged so the admin can
            // tell it apart — this is why "RefundIssued RMAs" can exceed "Refunded orders". Idempotent.
            order.PartiallyRefunded = true;
        }

        await db.SaveChangesAsync(context.CancellationToken);
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

        // A purchase is timeline-worthy activity (mt6_1): without this, the Mission Control
        // "Activity timeline" only ever shows admin mutations and confirmed orders are invisible.
        // Recorded here — the single place an order becomes Confirmed — and delivered through the
        // same consumer outbox as OrderConfirmed below, so it lands iff the confirmation does.
        // Guarded by the idempotency checks above: a redelivered CheckoutCompleted returns early.
        await audit.RecordAsync(PurchaseAudit(order), context.CancellationToken);

        await context.Publish(new OrderConfirmed(
            order.Id, order.TenantId, order.Email, order.GrossMinor, order.Currency,
            new ShipToInfo(order.ShipName, order.ShipLine1, order.ShipCity, order.ShipPostcode, order.ShipCountry),
            order.Lines.Select(l => new OrderLineInfo(
                l.ProductId, l.VariantId, l.SupplierId, l.Title, l.Quantity, l.FulfilmentType, l.BillingMode, l.UnitPriceMinor)).ToList()));

        // COGS accrual (phase 1): wires the previously-dormant SupplierPayable path. Order lines carry the
        // resolved SupplierId but not its cost — cost lives on the line's resolved Offer (projected into
        // OfferCopy), the same source that gave the line its SupplierId at checkout. Payments turns the
        // event into per-store COGS accruals.
        await PublishOrderCostsAsync(order, context);

        // Recurring lines set up a subscription in Payments (mt7_3); the first period was paid with the order.
        foreach (var line in order.Lines.Where(l => l.BillingMode == BillingMode.Recurring && l.BillingPeriod != BillingPeriod.Once))
        {
            await context.Publish(new SubscriptionRequested(
                order.TenantId, order.Id, order.Email, line.ProductId, line.VariantId, line.BillingPeriod, line.UnitPriceMinor, order.Currency,
                order.StorefrontId));
        }
    }

    /// <summary>
    /// Aggregates per-supplier gross cost of goods over the order's lines and publishes
    /// <see cref="OrderCostsRecognized"/> for Payments to accrue. Cost comes from the line's resolved
    /// <see cref="OfferCopy"/> (unit <see cref="OfferCopy.SupplierCostMinor"/> × quantity) — the same
    /// source that gave the line its SupplierId at checkout — NOT the dormant ProductCopy.SupplierCostMinor.
    /// The accrual is always denominated in the ORDER's currency. When an offer's cost was denominated in
    /// a different currency there is no FX feed to convert it, so — exactly as the carrier-cost consumer
    /// (ShippingLabelPurchasedConsumer) posts the fake carrier's AUD cost in the payment's currency — we
    /// treat the offer-denominated minor amount as order-currency minor units without conversion (a
    /// deliberate, logged relabel; a misleading 100% margin is worse dev data than an approximate cost).
    /// FX is explicitly out of scope (plan NOTES); revisit this and the carrier-cost consumer together when
    /// FX lands. A supplier line resolving to zero cost is logged, so a silent non-accrual is
    /// distinguishable from "no supplier lines". Nothing is published when the total is zero.
    /// </summary>
    private async Task PublishOrderCostsAsync(Order order, ConsumeContext context)
    {
        var supplierLines = order.Lines.Where(l => l.SupplierId is { } sid && sid != Guid.Empty).ToList();
        if (supplierLines.Count == 0)
        {
            return;
        }

        var productIds = supplierLines.Select(l => l.ProductId).Distinct().ToList();
        var offerCopies = await db.OfferCopies.AsNoTracking()
            .Where(o => o.TenantId == order.TenantId && productIds.Contains(o.ProductId))
            .ToListAsync(context.CancellationToken);

        var perSupplier = new Dictionary<Guid, long>();
        var relabelledFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in supplierLines)
        {
            var offer = OfferResolution.ResolveOffer(offerCopies, order.TenantId, line.ProductId, line.VariantId);
            if (offer is null)
            {
                continue;
            }

            var lineCost = offer.SupplierCostMinor * line.Quantity;
            if (lineCost <= 0)
            {
                logger.LogWarning(
                    "OrderCostsRecognized: order {OrderId} product {ProductId} resolved supplier {SupplierId} but zero supplier cost; no COGS accrued for this line",
                    order.Id, line.ProductId, offer.SupplierId);
                continue;
            }

            // No FX feed: an offer costed in another currency is relabelled into the order's currency
            // (see method doc). Collect the source denominations to warn once per order, not per line.
            if (!string.IsNullOrEmpty(offer.Currency)
                && !string.Equals(offer.Currency, order.Currency, StringComparison.OrdinalIgnoreCase))
            {
                relabelledFrom.Add(offer.Currency);
            }

            perSupplier[offer.SupplierId] = perSupplier.GetValueOrDefault(offer.SupplierId) + lineCost;
        }

        if (perSupplier.Count == 0)
        {
            return;
        }

        if (relabelledFrom.Count > 0)
        {
            logger.LogWarning(
                "OrderCostsRecognized: order {OrderId} accrues COGS in {OrderCurrency}; supplier cost(s) originally denominated in {SourceCurrencies} were relabelled without FX conversion (out of scope)",
                order.Id, order.Currency, string.Join(",", relabelledFrom));
        }

        var items = perSupplier.Select(kv => new SupplierCostItem(kv.Key, kv.Value)).ToList();
        await context.Publish(
            new OrderCostsRecognized(order.Id, order.StorefrontId, order.TenantId, order.Currency, items),
            context.CancellationToken);
    }

    /// <summary>
    /// The audit draft for a confirmed purchase — this is what the Mission Control activity
    /// timeline renders. The actor is the shopper (or "guest" when the order has no owner yet);
    /// the summary carries order number + amount only — the shopper's email is PII and stays out
    /// of the audit store (mt6_2 GOTCHA).
    /// </summary>
    public static AuditDraft PurchaseAudit(Order order) =>
        AuditCategories.Mutation(
            order.TenantId,
            order.UserId,
            order.UserId is null ? "guest" : "customer",
            "Order",
            order.Id.ToString(),
            "ordering.order.confirm",
            string.Create(CultureInfo.InvariantCulture, $"#{order.PublicOrderNumber} {Money.Amount(order.GrossMinor, order.Currency)} {order.Currency}"));

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
