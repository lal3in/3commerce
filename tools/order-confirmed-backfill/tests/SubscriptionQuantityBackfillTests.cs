using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Tools.OrderConfirmedBackfill.Subscriptions;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

/// <summary>
/// subscription_quantity_backfill (owner decision 2026-10-27, ADR-0057 amendment): a subscription set up before
/// quantity was carried renews at ONE unit's price less the whole line's renewal discount. The
/// <c>subscription-quantity</c> command finds exactly those (stored qty 1 at the legacy price), re-sends the line's
/// live <c>SubscriptionRequested</c> to Payments' own queue, and Payments corrects them; a re-run sends nothing.
/// </summary>
public class SubscriptionQuantityBackfillTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public void Selects_only_a_legacy_single_unit_subscription_at_the_legacy_price()
    {
        var legacy = RecurringOrder(qty: 3, unit: 2000, renewalDiscount: 1500);          // legacy price 500, correct 4500
        var current = RecurringOrder(qty: 2, unit: 1000, renewalDiscount: 0);
        var none = RecurringOrder(qty: 2, unit: 1000, renewalDiscount: 0);
        var cancelled = RecurringOrder(qty: 2, unit: 1000, renewalDiscount: 0);
        var odd = RecurringOrder(qty: 2, unit: 1000, renewalDiscount: 0);
        var single = RecurringOrder(qty: 1, unit: 1000, renewalDiscount: 0);              // never in scope
        var subscriptions = new List<Subscription>
        {
            SubscriptionFor(legacy, quantity: 1, price: 500),
            SubscriptionFor(current, quantity: 2, price: 2000),
            Cancel(SubscriptionFor(cancelled, quantity: 1, price: 1000)),
            SubscriptionFor(odd, quantity: 1, price: 900),                              // not the legacy 1000
            SubscriptionFor(single, quantity: 1, price: 1000),
        };

        var verdicts = SubscriptionQuantityPlanner.Plan(Tenant, [legacy, current, none, cancelled, odd, single], subscriptions)
            .ToDictionary(v => v.Order.Id);

        Assert.Equal(5, verdicts.Count);
        Assert.Equal(QuantityOutcome.ToCorrect, verdicts[legacy.Id].Outcome);
        Assert.Equal(500, verdicts[legacy.Id].LegacyPriceMinor);
        Assert.Equal(4500, verdicts[legacy.Id].CorrectPriceMinor);
        Assert.Equal(QuantityOutcome.AlreadyCorrect, verdicts[current.Id].Outcome);
        Assert.Equal(QuantityOutcome.NoSubscription, verdicts[none.Id].Outcome);
        Assert.Equal(QuantityOutcome.Cancelled, verdicts[cancelled.Id].Outcome);
        Assert.Equal(QuantityOutcome.UnexpectedPrice, verdicts[odd.Id].Outcome);
        Assert.False(verdicts.ContainsKey(single.Id));
    }

    [Fact]
    public void The_resent_request_is_the_live_mapping_with_the_subscriptions_own_tax_terms()
    {
        var order = RecurringOrder(qty: 3, unit: 2000, renewalDiscount: 1500);
        var subscription = SubscriptionFor(order, quantity: 1, price: 500, taxInclusive: false, taxExempt: true);
        var verdict = Assert.Single(SubscriptionQuantityPlanner.Plan(Tenant, [order], [subscription]));

        var request = SubscriptionQuantityPlanner.Rebuild(verdict);

        Assert.Equal(4500, request.PriceMinor);
        Assert.Equal(3, request.Quantity);
        Assert.Equal((order.Id, order.Lines[0].ProductId, order.Lines[0].VariantId), (request.OrderId, request.ProductId, request.VariantId));
        Assert.False(request.TaxInclusive);
        Assert.True(request.TaxExempt);
        Assert.Equal(order.Currency, request.Currency);
    }

    [Fact]
    public void Limit_caps_the_sends_not_the_counts()
    {
        var orders = Enumerable.Range(0, 3).Select(_ => RecurringOrder(qty: 2, unit: 1000, renewalDiscount: 0)).ToList();
        var subscriptions = orders.Select(o => SubscriptionFor(o, quantity: 1, price: 1000)).ToList();

        var plan = SubscriptionQuantityPlanner.From(SubscriptionQuantityPlanner.Plan(Tenant, orders, subscriptions), limit: 2);

        Assert.Equal(3, plan.Count(QuantityOutcome.ToCorrect));
        Assert.Equal(2, plan.ToSend.Count);
    }

    [Fact]
    public async Task Payments_applies_the_resent_request_once_and_a_rerun_selects_nothing()
    {
        var order = RecurringOrder(qty: 2, unit: 1000, renewalDiscount: 250);   // legacy 750, correct 1750
        await using var ordering = BackfillFixtures.OrderingDb($"sq_o_{Guid.NewGuid():N}");
        ordering.Orders.Add(order);
        await ordering.SaveChangesAsync();
        await using var payments = new PaymentsDbContext(
            new DbContextOptionsBuilder<PaymentsDbContext>().UseInMemoryDatabase($"sq_p_{Guid.NewGuid():N}").Options);
        payments.Subscriptions.Add(SubscriptionFor(order, quantity: 1, price: 750));
        await payments.SaveChangesAsync();

        var runner = new SubscriptionQuantityRunner(
            new OrderSource(ordering, new PassThroughTenantScope()), new PaymentsSubscriptionSource(payments, new PassThroughTenantScope()));
        var options = SubscriptionQuantityOptions.Parse(["--dry-run"]);
        var first = await runner.PlanAsync(options, default);
        var toSend = Assert.Single(first.ToSend);

        // What Payments' SubscriptionRequestedConsumer does with the sent message — delivered twice (at-least-once).
        var service = PaymentsSubscriptionService(payments);
        await service.StartAsync(SubscriptionQuantityPlanner.Rebuild(toSend), default);
        await service.StartAsync(SubscriptionQuantityPlanner.Rebuild(toSend), default);

        var stored = await payments.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(2, stored.Quantity);
        Assert.Equal(1750, stored.PriceMinor);
        var rerun = await runner.PlanAsync(options, default);
        Assert.Empty(rerun.ToSend);
        Assert.Equal(QuantityOutcome.AlreadyCorrect, Assert.Single(rerun.Verdicts).Outcome);
    }

    [Fact]
    public void Options_require_an_explicit_mode_and_parse_the_rest()
    {
        var tenant = Guid.NewGuid();
        var o = SubscriptionQuantityOptions.Parse(["--execute", "--tenant", tenant.ToString(), "--limit", "5", "--list", "--skip-preflight"]);
        Assert.True(o.Execute);
        Assert.Equal([tenant], o.Tenants);
        Assert.Equal(5, o.Limit);
        Assert.True(o.List && o.SkipPreflight);

        var dry = SubscriptionQuantityOptions.Parse(["--dry-run"]);
        Assert.False(dry.Execute || dry.SkipPreflight);
        Assert.Throws<BackfillUsageException>(() => SubscriptionQuantityOptions.Parse([]));
        Assert.Throws<BackfillUsageException>(() => SubscriptionQuantityOptions.Parse(["--dry-run", "--execute"]));
        Assert.Throws<BackfillUsageException>(() => SubscriptionQuantityOptions.Parse(["--dry-run", "--limit", "0"]));
    }

    [Fact]
    public void Sends_go_to_Payments_own_queue_with_a_stable_message_id()
    {
        Assert.Equal("subscription-requested", SubscriptionQuantityQueues.QueueName);
        Assert.Equal(new Uri("queue:subscription-requested"), SubscriptionQuantityQueues.Address);
        var id = Guid.NewGuid();
        Assert.Equal(SubscriptionQuantityQueues.MessageId(id), SubscriptionQuantityQueues.MessageId(id));
        Assert.NotEqual(SubscriptionQuantityQueues.MessageId(id), SubscriptionQuantityQueues.MessageId(Guid.NewGuid()));
    }

    // ---- Helpers --------------------------------------------------------------------------------------

    /// <summary>StartAsync touches only the DbContext and the clock; the rail, mode resolver and publisher are unused.</summary>
    private static SubscriptionService PaymentsSubscriptionService(PaymentsDbContext db) =>
        new(db, null!, null!, TimeProvider.System, NullLogger<SubscriptionService>.Instance, null!);

    private static Order RecurringOrder(int qty, long unit, long renewalDiscount)
    {
        var order = BackfillFixtures.MakeOrder(Tenant, BackfillFixtures.T0, "subscriber@example.com", OrderStatus.Confirmed, FulfilmentType.DigitalDownload);
        var line = order.Lines[0];
        order.Lines[0] = new OrderLine
        {
            Id = line.Id,
            OrderId = order.Id,
            ProductId = line.ProductId,
            VariantId = Guid.NewGuid(),
            Title = "Plan",
            UnitPriceMinor = unit,
            Quantity = qty,
            DiscountMinor = renewalDiscount,
            RenewalDiscountMinor = renewalDiscount,
            FulfilmentType = FulfilmentType.DigitalDownload,
            BillingMode = BillingMode.Recurring,
            BillingPeriod = BillingPeriod.Monthly,
        };
        return order;
    }

    private static Subscription SubscriptionFor(Order order, int quantity, long price, bool? taxInclusive = true, bool taxExempt = false)
    {
        var line = order.Lines[0];
        return Subscription.Start(
            Tenant, order.Id, order.Email, line.ProductId, line.VariantId, BillingPeriod.Monthly, price, order.Currency,
            BackfillFixtures.T0, taxInclusive: taxInclusive, taxExempt: taxExempt, quantity: quantity);
    }

    private static Subscription Cancel(Subscription subscription)
    {
        subscription.Cancel(BackfillFixtures.T0);
        return subscription;
    }
}
