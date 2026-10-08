using ThreeCommerce.Workers.Notifications.Domain;
using ThreeCommerce.Workers.Notifications.Email;
using static ThreeCommerce.Tools.OrderConfirmedBackfill.Tests.BackfillFixtures;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

/// <summary>
/// What the Notifications delivery log proves per order. The invariant every case protects: an order is only ever
/// "Missing" (and so emailed by default) when no logged email could have been its own — a wrong Missing re-emails a
/// customer.
/// </summary>
public class EmailEvidenceMatcherTests
{
    private static readonly EmailMatchOptions Options = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(30));

    private static EmailEvidence Classify(OrderFacts order, IReadOnlyCollection<OrderFacts> orders, IReadOnlyCollection<DeliveryFact> deliveries, DateTimeOffset? logStart = null) =>
        EmailEvidenceMatcher.Classify(orders, deliveries, logStart ?? T0.AddDays(-1), Options).ByOrder[order.OrderId];

    [Fact]
    public void A_referenced_delivery_proves_its_order_exactly_whenever_it_was_sent()
    {
        var order = Facts("a@x.test", T0);
        var late = Sent("someone-else@x.test", T0.AddDays(30), NotificationReferences.OrderConfirmed(order.OrderId));

        Assert.Equal(EmailEvidence.EmailedByReference, Classify(order, [order], [late]));
    }

    [Fact]
    public void A_legacy_delivery_inside_the_window_matches_the_only_candidate_order()
    {
        var order = Facts("a@x.test", T0);
        var result = EmailEvidenceMatcher.Classify([order], [Sent("A@X.test ", T0.AddMilliseconds(400))], T0.AddDays(-1), Options);

        Assert.Equal(EmailEvidence.EmailedByTimeMatch, result.ByOrder[order.OrderId]); // recipient matched case/space-insensitively
        Assert.Equal(TimeSpan.FromMilliseconds(400), Assert.Single(result.MatchedLags));
        Assert.Equal(0, result.OrphanDeliveries);
    }

    [Fact]
    public void An_order_with_no_delivery_in_its_window_is_missing()
    {
        var emailed = Facts("a@x.test", T0);
        var missed = Facts("a@x.test", T0.AddMinutes(5));

        Assert.Equal(EmailEvidence.Missing, Classify(missed, [emailed, missed], [Sent("a@x.test", T0.AddSeconds(1))]));
    }

    [Fact]
    public void Back_to_back_orders_with_one_email_between_them_are_ambiguous_not_missing()
    {
        // One email lands where it could be either order's: one of the two got it, the log cannot say which.
        var first = Facts("a@x.test", T0);
        var second = Facts("a@x.test", T0.AddSeconds(3));
        var deliveries = new[] { Sent("a@x.test", T0.AddSeconds(4)) };

        Assert.Equal(EmailEvidence.Ambiguous, Classify(first, [first, second], deliveries));
        Assert.Equal(EmailEvidence.Ambiguous, Classify(second, [first, second], deliveries));
    }

    [Fact]
    public void Certain_matches_propagate_to_resolve_neighbours()
    {
        // d1 can only be o1's; once o1 is accounted for, d2 can only be o2's; o3 has no delivery → Missing.
        var o1 = Facts("a@x.test", T0);
        var o2 = Facts("a@x.test", T0.AddSeconds(5));
        var o3 = Facts("a@x.test", T0.AddSeconds(30));
        var deliveries = new[] { Sent("a@x.test", T0.AddSeconds(1)), Sent("a@x.test", T0.AddSeconds(9)) };
        var result = EmailEvidenceMatcher.Classify([o1, o2, o3], deliveries, T0.AddDays(-1), Options);

        Assert.Equal(EmailEvidence.EmailedByTimeMatch, result.ByOrder[o1.OrderId]);
        Assert.Equal(EmailEvidence.EmailedByTimeMatch, result.ByOrder[o2.OrderId]);
        Assert.Equal(EmailEvidence.Missing, result.ByOrder[o3.OrderId]);
    }

    [Fact]
    public void An_unexplained_late_email_makes_earlier_missing_orders_of_that_address_ambiguous()
    {
        // The email arrived 5 minutes after the order — outside the window. It may well be this order's, sent late
        // (worker was down), so the order must not be re-emailed on the strength of the time model.
        var order = Facts("a@x.test", T0);
        var result = EmailEvidenceMatcher.Classify([order], [Sent("a@x.test", T0.AddMinutes(5))], T0.AddDays(-1), Options);

        Assert.Equal(EmailEvidence.Ambiguous, result.ByOrder[order.OrderId]);
        Assert.Equal(1, result.OrphanDeliveries);
    }

    [Fact]
    public void An_orphan_only_affects_orders_within_its_reach()
    {
        var old = Facts("a@x.test", T0);
        var recent = Facts("a@x.test", T0.AddHours(5));
        var orphan = Sent("a@x.test", T0.AddHours(5).AddMinutes(3)); // could be `recent`'s, cannot be `old`'s

        Assert.Equal(EmailEvidence.Missing, Classify(old, [old, recent], [orphan]));
        Assert.Equal(EmailEvidence.Ambiguous, Classify(recent, [old, recent], [orphan]));
    }

    [Fact]
    public void Failed_deliveries_are_not_evidence_of_an_email()
    {
        var order = Facts("a@x.test", T0);
        var failed = new DeliveryFact("a@x.test", NotificationStatus.Failed, T0.AddSeconds(1), null);

        Assert.Equal(EmailEvidence.Missing, Classify(order, [order], [failed]));
    }

    [Fact]
    public void Another_address_email_never_counts()
    {
        var order = Facts("a@x.test", T0);
        var result = EmailEvidenceMatcher.Classify([order], [Sent("b@x.test", T0.AddSeconds(1))], T0.AddDays(-1), Options);

        Assert.Equal(EmailEvidence.Missing, result.ByOrder[order.OrderId]);
        Assert.Equal(1, result.OrphanDeliveries); // reported, but it cannot be this order's
    }

    [Fact]
    public void Orders_confirmed_before_the_log_existed_have_no_evidence()
    {
        var order = Facts("a@x.test", T0);

        Assert.Equal(EmailEvidence.PredatesDeliveryLog, Classify(order, [order], [], logStart: T0.AddHours(1)));
        Assert.Equal(EmailEvidence.PredatesDeliveryLog, EmailEvidenceMatcher.Classify([order], [], null, Options).ByOrder[order.OrderId]);
    }

    [Fact]
    public void A_matched_order_before_the_log_start_still_counts_as_emailed()
    {
        var order = Facts("a@x.test", T0);

        Assert.Equal(EmailEvidence.EmailedByTimeMatch, Classify(order, [order], [Sent("a@x.test", T0.AddSeconds(1))], logStart: T0.AddSeconds(1)));
    }

    [Fact]
    public void Order_references_parse_only_the_confirmation_prefix()
    {
        var id = Guid.CreateVersion7();

        Assert.True(EmailEvidenceMatcher.TryParseOrderReference(NotificationReferences.OrderConfirmed(id), out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(EmailEvidenceMatcher.TryParseOrderReference($"tracking-assigned:{id}", out _));
        Assert.False(EmailEvidenceMatcher.TryParseOrderReference(null, out _));
        Assert.False(EmailEvidenceMatcher.TryParseOrderReference("order-confirmed:not-a-guid", out _));
    }
}
