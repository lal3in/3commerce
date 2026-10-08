using ThreeCommerce.Ordering.Domain;
using static ThreeCommerce.Tools.OrderConfirmedBackfill.Tests.BackfillFixtures;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

/// <summary>Which orders each target is sent — eligibility, Fulfillment's own records, the email evidence, --limit.</summary>
public class BackfillPlannerTests
{
    private static EmailEvidenceResult Evidence(params (OrderFacts Order, EmailEvidence Evidence)[] entries) =>
        new(entries.ToDictionary(e => e.Order.OrderId, e => e.Evidence), 0, []);

    [Fact]
    public void Fulfillment_gets_eligible_shippable_orders_it_has_no_shipment_or_held_order_for()
    {
        var missing = Facts("a@x.test", T0);
        var shipped = Facts("a@x.test", T0.AddMinutes(1));
        var digitalOnly = Facts("a@x.test", T0.AddMinutes(2), shippable: false);

        var plan = BackfillPlanner.Plan([missing, shipped, digitalOnly], new HashSet<Guid> { shipped.OrderId }, Evidence(), false, null);

        Assert.Equal([missing.OrderId], plan.Fulfillment.Select(o => o.OrderId));
        Assert.Equal(2, plan.ShippableEligible);
        Assert.Equal(1, plan.FulfillmentAlreadySeen);
        Assert.Equal(1, plan.FulfillmentMissing);
    }

    [Theory]
    [InlineData(OrderStatus.Refunded, false, SkipReason.Refunded)]
    [InlineData(OrderStatus.Delivered, false, SkipReason.Delivered)]
    [InlineData(OrderStatus.Confirmed, true, SkipReason.Disputed)]
    public void Refunded_delivered_and_disputed_orders_are_never_sent(OrderStatus status, bool disputed, SkipReason reason)
    {
        var order = Facts("a@x.test", T0, status: status, disputed: disputed);

        var plan = BackfillPlanner.Plan([order], new HashSet<Guid>(), Evidence((order, EmailEvidence.Missing)), true, null);

        Assert.Empty(plan.Fulfillment);
        Assert.Empty(plan.Notifications);
        Assert.Equal(1, plan.Skipped[reason]);
    }

    [Fact]
    public void Notifications_get_only_proven_missing_orders_unless_unproven_is_opted_into()
    {
        var missing = Facts("a@x.test", T0);
        var ambiguous = Facts("b@x.test", T0);
        var predates = Facts("c@x.test", T0);
        var emailed = Facts("d@x.test", T0);
        var evidence = Evidence(
            (missing, EmailEvidence.Missing), (ambiguous, EmailEvidence.Ambiguous),
            (predates, EmailEvidence.PredatesDeliveryLog), (emailed, EmailEvidence.EmailedByTimeMatch));
        IReadOnlyCollection<OrderFacts> all = [missing, ambiguous, predates, emailed];

        var strict = BackfillPlanner.Plan(all, new HashSet<Guid>(), evidence, false, null);
        var loose = BackfillPlanner.Plan(all, new HashSet<Guid>(), evidence, true, null);

        Assert.Equal([missing.OrderId], strict.Notifications.Select(o => o.OrderId));
        Assert.Equal(
            new[] { missing.OrderId, ambiguous.OrderId, predates.OrderId }.Order(),
            loose.Notifications.Select(o => o.OrderId).Order());
        Assert.Equal(1, strict.EmailEvidenceCounts[EmailEvidence.EmailedByTimeMatch]);
    }

    [Fact]
    public void Without_email_evidence_nothing_is_selected_for_notifications()
    {
        var plan = BackfillPlanner.Plan([Facts("a@x.test", T0)], new HashSet<Guid>(), Evidence(), true, null);

        Assert.Empty(plan.Notifications);
        Assert.Empty(plan.EmailEvidenceCounts);
    }

    [Fact]
    public void Limit_caps_the_sends_oldest_first_but_not_the_totals()
    {
        var orders = Enumerable.Range(0, 5).Select(i => Facts("a@x.test", T0.AddMinutes(5 - i))).ToList();

        var plan = BackfillPlanner.Plan(orders, new HashSet<Guid>(), Evidence(), false, 2);

        Assert.Equal(5, plan.FulfillmentMissing);
        Assert.Equal(orders.OrderBy(o => o.ConfirmedAt).Take(2).Select(o => o.OrderId), plan.Fulfillment.Select(o => o.OrderId));
    }
}
