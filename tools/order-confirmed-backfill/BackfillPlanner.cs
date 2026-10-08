using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill;

/// <summary>Counts and the orders to send, per target (<see cref="Fulfillment"/>/<see cref="Notifications"/> are capped by --limit; the totals are not). Pure — sends nothing.</summary>
public sealed record BackfillPlan(
    int ConfirmedOrders,
    IReadOnlyDictionary<SkipReason, int> Skipped,
    int ShippableEligible,
    int FulfillmentAlreadySeen,
    int FulfillmentMissing,
    IReadOnlyList<OrderFacts> Fulfillment,
    IReadOnlyDictionary<EmailEvidence, int> EmailEvidenceCounts,
    int NotificationsSelected,
    IReadOnlyList<OrderFacts> Notifications,
    EmailEvidenceResult Email);

/// <summary>
/// Which orders each consumer missed (ADR-0060). An order is sent only when it is eligible (<see cref="Eligibility"/>)
/// AND the target's own durable record says it never processed it:
/// <list type="bullet">
/// <item><b>Fulfillment</b>: the order has a line Fulfillment ships (<c>RequiresShipping</c>) and Fulfillment holds
/// neither a <c>Shipment</c> nor a captured <c>HeldOrder</c> for it — exactly the two records
/// <c>FulfillmentOrderConfirmedConsumer</c> checks before doing anything, so a resend is a no-op otherwise.</item>
/// <item><b>Notifications</b>: the delivery log proves no confirmation email went out
/// (<see cref="EmailEvidence.Missing"/>); with <c>includeUnprovenEmail</c> also Ambiguous / PredatesDeliveryLog,
/// which WILL re-email some customers.</item>
/// </list>
/// Re-running after a send selects only what is still missing: Fulfillment then has the shipment / held order, and
/// the worker records the new email with its <c>order-confirmed:{id}</c> reference.
/// </summary>
public static class BackfillPlanner
{
    /// <summary>The statuses an order reaches only after it was confirmed (an <c>OrderConfirmed</c> was published).</summary>
    public static readonly OrderStatus[] ConfirmedFamily = [OrderStatus.Confirmed, OrderStatus.Refunded, OrderStatus.Delivered];

    public static SkipReason Eligibility(OrderFacts order) => order.Status switch
    {
        OrderStatus.Refunded => SkipReason.Refunded,
        OrderStatus.Delivered => SkipReason.Delivered,
        OrderStatus.Confirmed when order.Disputed => SkipReason.Disputed,
        OrderStatus.Confirmed => SkipReason.None,
        _ => throw new ArgumentOutOfRangeException(nameof(order), order.Status, "Only confirmed-family orders are planned."),
    };

    public static BackfillPlan Plan(
        IReadOnlyCollection<OrderFacts> orders,
        IReadOnlySet<Guid> seenByFulfillment,
        EmailEvidenceResult email,
        bool includeUnprovenEmail,
        int? limit)
    {
        var eligible = orders.Where(o => Eligibility(o) == SkipReason.None).OrderBy(o => o.ConfirmedAt).ThenBy(o => o.OrderId).ToList();
        var skipped = orders.Select(Eligibility).Where(r => r != SkipReason.None)
            .GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());

        var shippable = eligible.Where(o => o.HasShippableLine).ToList();
        var fulfillment = shippable.Where(o => !seenByFulfillment.Contains(o.OrderId)).ToList();

        // No evidence entry = the delivery log was not read (notifications not targeted): never select.
        var notifications = eligible.Where(o => email.ByOrder.TryGetValue(o.OrderId, out var evidence) && evidence switch
        {
            EmailEvidence.Missing => true,
            EmailEvidence.Ambiguous or EmailEvidence.PredatesDeliveryLog => includeUnprovenEmail,
            _ => false,
        }).ToList();

        var evidenceCounts = eligible.Where(o => email.ByOrder.ContainsKey(o.OrderId))
            .GroupBy(o => email.ByOrder[o.OrderId]).ToDictionary(g => g.Key, g => g.Count());

        return new BackfillPlan(
            orders.Count,
            skipped,
            shippable.Count,
            shippable.Count - fulfillment.Count,
            fulfillment.Count,
            Cap(fulfillment, limit),
            evidenceCounts,
            notifications.Count,
            Cap(notifications, limit),
            email);
    }

    private static List<OrderFacts> Cap(List<OrderFacts> orders, int? limit) =>
        limit is { } n ? orders.Take(n).ToList() : orders;
}
