using ThreeCommerce.Tools.OrderConfirmedBackfill.SupportOwners;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

/// <summary>
/// The <c>support-order-owner</c> command (ADR-0018 amendment 2026-11-27): an ownerless Support snapshot whose Ordering
/// order has a user is sent; a guest order or an order Ordering does not know is reported and never sent; the target
/// queue is Support's own snapshot queue, derived from the consumer type; sending is never the default.
/// </summary>
public sealed class SupportOrderOwnerBackfillTests
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    [Fact]
    public void Only_orders_with_a_user_are_sent_and_guests_and_unknown_orders_are_reported()
    {
        var (owned, guest, unknown) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var plan = SupportOrderOwnerPlanner.Plan(
            [owned, guest, unknown],
            [new OrderOwnerFact(owned, TenantA, Guid.CreateVersion7()), new OrderOwnerFact(guest, TenantA, null)],
            new HashSet<Guid>(), limit: null);

        Assert.Equal(1, plan.Missing);
        Assert.Equal(1, plan.Count(OwnerOutcome.Guest));
        Assert.Equal(1, plan.Count(OwnerOutcome.NoOrder));
        Assert.Equal(owned, Assert.Single(plan.ToSend).OrderId);
    }

    [Fact]
    public void Tenant_filter_and_limit_narrow_the_sends_oldest_first()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToList();
        var other = Guid.CreateVersion7();
        var facts = ids.Select(id => new OrderOwnerFact(id, TenantA, Guid.CreateVersion7()))
            .Append(new OrderOwnerFact(other, TenantB, Guid.CreateVersion7())).ToList();

        var plan = SupportOrderOwnerPlanner.Plan([.. ids, other], facts, new HashSet<Guid> { TenantA }, limit: 2);

        Assert.Equal(3, plan.Missing);
        Assert.Equal(ids.Order().Take(2), plan.ToSend.Select(v => v.OrderId));
        Assert.Equal(OwnerOutcome.NoOrder, plan.Verdicts.Single(v => v.OrderId == other).Outcome);
    }

    [Fact]
    public void Sends_go_to_supports_own_snapshot_queue_with_a_deterministic_id()
    {
        Assert.Equal("order-snapshot", SupportOrderOwnerQueues.QueueName);
        Assert.Equal(new Uri("queue:order-snapshot"), SupportOrderOwnerQueues.Address);
        var order = Guid.CreateVersion7();
        Assert.Equal(SupportOrderOwnerQueues.MessageId(order), SupportOrderOwnerQueues.MessageId(order));
        Assert.NotEqual(SupportOrderOwnerQueues.MessageId(order), SupportOrderOwnerQueues.MessageId(Guid.CreateVersion7()));
    }

    [Fact]
    public void Sending_is_never_the_default()
    {
        Assert.Throws<BackfillUsageException>(() => SupportOrderOwnerOptions.Parse([]));
        Assert.Throws<BackfillUsageException>(() => SupportOrderOwnerOptions.Parse(["--dry-run", "--execute"]));
        var options = SupportOrderOwnerOptions.Parse(["--execute", "--limit", "5", "--tenant", TenantA.ToString(), "--list"]);
        Assert.True(options.Execute);
        Assert.Equal(5, options.Limit);
        Assert.Contains(TenantA, options.Tenants);
        Assert.True(options.List);
    }
}
