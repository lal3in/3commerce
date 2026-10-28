using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure.Consumers;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Subscriptions;

/// <summary>What the planner decided for one multi-unit subscription line.</summary>
public enum QuantityOutcome
{
    /// <summary>A legacy single-unit subscription priced the pre-fix way: re-send the line's facts.</summary>
    ToCorrect = 1,

    /// <summary>The subscription already carries the line's quantity — nothing to do (also after a successful run).</summary>
    AlreadyCorrect = 2,

    /// <summary>Payments has no subscription for the line (never set up, or another tenant's) — reported, not sent.</summary>
    NoSubscription = 3,

    /// <summary>The subscription is cancelled: it never renews again, so there is nothing to re-price.</summary>
    Cancelled = 4,

    /// <summary>
    /// Stored as one unit but NOT at the pre-fix price (unit − the line's renewal discount): something else changed it,
    /// so the tool does not guess — reported for an operator, never sent.
    /// </summary>
    UnexpectedPrice = 5,
}

/// <summary>One recurring order line with quantity &gt; 1 and the subscription Payments holds for it.</summary>
public sealed record QuantityVerdict(
    Guid TenantId,
    Order Order,
    OrderLine Line,
    Subscription? Subscription,
    QuantityOutcome Outcome)
{
    /// <summary>What the subscription was priced at by the pre-fix mapping: one unit less the whole line's renewal discount.</summary>
    public long LegacyPriceMinor => Math.Max(0, Line.UnitPriceMinor - Line.RenewalDiscountMinor);

    /// <summary>The correct renewal price — the live mapping's (<see cref="OrderLine.RenewalPriceMinor"/>).</summary>
    public long CorrectPriceMinor => Line.RenewalPriceMinor();
}

/// <summary>Counts and the verdicts to send (capped by --limit; the counts are not). Pure — sends nothing.</summary>
public sealed record SubscriptionQuantityPlan(IReadOnlyList<QuantityVerdict> Verdicts, IReadOnlyList<QuantityVerdict> ToSend)
{
    public int Count(QuantityOutcome outcome) => Verdicts.Count(v => v.Outcome == outcome);
}

/// <summary>
/// Which existing subscriptions to re-price (owner decision 2026-10-27, ADR-0057 amendment,
/// <c>subscription_quantity_backfill</c>). Before quantity was carried, a recurring line of quantity n set up its
/// subscription at <c>unit − the line's renewal discount</c> for ONE unit. A line is selected only when Payments still
/// holds exactly that: <see cref="Subscription.Quantity"/> = 1 and <see cref="Subscription.PriceMinor"/> = the legacy
/// price. Its facts are then re-sent as the live <see cref="SubscriptionRequested"/> (built by
/// <see cref="OrderStatusConsumer.SubscriptionRequestFor"/>, the mapping the confirmation path publishes with), which
/// Payments applies through <see cref="Subscription.CorrectLegacyQuantity"/>. A corrected subscription carries its
/// quantity, so a re-run reports it <see cref="QuantityOutcome.AlreadyCorrect"/> and sends nothing.
/// </summary>
public static class SubscriptionQuantityPlanner
{
    public static IEnumerable<QuantityVerdict> Plan(Guid tenantId, IEnumerable<Order> orders, IReadOnlyCollection<Subscription> subscriptions)
    {
        var byLine = subscriptions.ToLookup(s => (s.OrderId, s.ProductId, s.VariantId));
        foreach (var order in orders.OrderBy(o => o.CreatedAt))
        {
            foreach (var line in order.Lines.Where(IsMultiUnitRecurring))
            {
                var subscription = byLine[(order.Id, line.ProductId, line.VariantId)].FirstOrDefault();
                yield return new QuantityVerdict(tenantId, order, line, subscription, Decide(line, subscription));
            }
        }
    }

    public static SubscriptionQuantityPlan From(IEnumerable<QuantityVerdict> verdicts, int? limit)
    {
        var all = verdicts.ToList();
        var toSend = all.Where(v => v.Outcome == QuantityOutcome.ToCorrect);
        return new SubscriptionQuantityPlan(all, (limit is { } n ? toSend.Take(n) : toSend).ToList());
    }

    /// <summary>The request Payments receives: the live mapping, with the subscription's own snapshotted tax terms.</summary>
    public static SubscriptionRequested Rebuild(QuantityVerdict verdict) =>
        verdict is { Outcome: QuantityOutcome.ToCorrect, Subscription: { } subscription }
            ? OrderStatusConsumer.SubscriptionRequestFor(verdict.Order, verdict.Line, subscription.TaxInclusive, subscription.TaxExempt)
            : throw new InvalidOperationException($"order {verdict.Order.Id} line {verdict.Line.ProductId} is not to be corrected");

    private static bool IsMultiUnitRecurring(OrderLine line) =>
        line.BillingMode == BillingMode.Recurring && line.BillingPeriod != BillingPeriod.Once && line.Quantity > 1;

    private static QuantityOutcome Decide(OrderLine line, Subscription? subscription) => subscription switch
    {
        null => QuantityOutcome.NoSubscription,
        { Quantity: var q } when q == line.Quantity => QuantityOutcome.AlreadyCorrect,
        { Status: SubscriptionStatus.Cancelled } => QuantityOutcome.Cancelled,
        { Quantity: 1 } s when s.PriceMinor == Math.Max(0, line.UnitPriceMinor - line.RenewalDiscountMinor) => QuantityOutcome.ToCorrect,
        _ => QuantityOutcome.UnexpectedPrice,
    };
}
