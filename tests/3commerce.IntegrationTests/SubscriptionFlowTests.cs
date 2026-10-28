using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt7_3: a recurring line sets up a subscription; renew charges + advances; cancel ends it.</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class SubscriptionFlowTests(Phase4Fixture fixture)
{
    private async Task<List<Subscription>> ListAsync(Guid tenant)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SubscriptionService>().ListAsync(tenant, null, default);
    }

    private async Task<Subscription?> ActAsync(Guid tenant, Guid id, bool cancel)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
        return cancel ? await service.CancelAsync(tenant, id, default) : await service.RenewAsync(tenant, id, default);
    }

    private async Task<Subscription?> GetAsync(Guid tenant, Guid id)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SubscriptionService>().GetAsync(tenant, id, default);
    }

    [Fact]
    public async Task A_resent_request_with_the_line_quantity_reprices_a_legacy_subscription_once()
    {
        // subscription_quantity_backfill (ADR-0057 amendment 2026-10-27): a qty-2 line set up before quantity was carried
        // is stored as ONE unit at unit − the line's renewal discount (1000 − 250 = 750). The backfill tool re-sends the
        // live request (2 × 1000 − 250 = 1750, Quantity 2); Payments corrects it once, then the renewal charges 1750.
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        await fixture.PublishAsync(new SubscriptionRequested(
            tenant, orderId, "legacy-qty@example.com", product, null, BillingPeriod.Monthly, 750, "EUR"));
        var legacy = await WaitForAsync(tenant, s => s.PriceMinor == 750);
        Assert.Equal(1, legacy.Quantity);

        var corrected = new SubscriptionRequested(
            tenant, orderId, "legacy-qty@example.com", product, null, BillingPeriod.Monthly, 1750, "EUR", Quantity: 2);
        await fixture.PublishAsync(corrected);
        var fixedUp = await WaitForAsync(tenant, s => s.Quantity == 2);
        Assert.Equal(legacy.Id, fixedUp.Id);                          // corrected in place, not a second subscription
        Assert.Equal(1750, fixedUp.PriceMinor);

        Assert.Single(await ListAsync(tenant));
        // (A repeat, or a different quantity, never re-prices a carried quantity — pinned by SubscriptionTests.)

        var renewed = await ActAsync(tenant, legacy.Id, cancel: false);
        Assert.Equal(SubscriptionStatus.Active, renewed!.Status);
        Assert.Equal(1750, (await GetAsync(tenant, legacy.Id))!.Renewals.Single(r => r.Sequence == 2).AmountMinor);
    }

    // Awake-time budget (Eventually): a Mac sleep mid-wait cannot expire it (engineering-gotchas, integration waits).
    private async Task<Subscription> WaitForAsync(Guid tenant, Func<Subscription, bool> match)
    {
        Subscription? hit = null;
        await Eventually.UntilAsync(
            async () => (hit = (await ListAsync(tenant)).FirstOrDefault(match)) is not null,
            () => new TimeoutException("subscription did not reach the expected state"),
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(300));
        return hit!;
    }

    [Fact]
    public async Task SubscriptionRequested_sets_up_then_renew_advances_then_cancel_ends()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await fixture.PublishAsync(new SubscriptionRequested(
            tenant, orderId, "buyer@example.com", product, null, BillingPeriod.Monthly, 1500, "EUR"));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        List<Subscription> subs;
        while (true)
        {
            subs = await ListAsync(tenant);
            if (subs.Count == 1 || DateTimeOffset.UtcNow > deadline)
            {
                break;
            }

            await Task.Delay(300);
        }

        Assert.Single(subs);
        var subscription = subs[0];
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        var firstPeriodEnd = subscription.CurrentPeriodEnd;

        // Setup records the first period (Sequence 1) so the timeline begins at signup.
        var afterSetup = await GetAsync(tenant, subscription.Id);
        var firstPeriod = Assert.Single(afterSetup!.Renewals);
        Assert.Equal(1, firstPeriod.Sequence);
        Assert.Equal(subscription.CurrentPeriodStart, firstPeriod.PeriodStart);
        Assert.Equal(1500, firstPeriod.AmountMinor);

        // Renew charges the next period via the rail (Fake) and advances.
        var renewed = await ActAsync(tenant, subscription.Id, cancel: false);
        Assert.Equal(SubscriptionStatus.Active, renewed!.Status);
        Assert.True(renewed.CurrentPeriodEnd > firstPeriodEnd);

        // Renew appends a history row: the sequence increments and the new period is recorded.
        var afterRenew = await GetAsync(tenant, subscription.Id);
        Assert.Equal(2, afterRenew!.Renewals.Count);
        Assert.Equal(new[] { 1, 2 }, afterRenew.Renewals.OrderBy(r => r.Sequence).Select(r => r.Sequence).ToArray());
        var secondPeriod = afterRenew.Renewals.Single(r => r.Sequence == 2);
        Assert.Equal(firstPeriodEnd, secondPeriod.PeriodStart);
        Assert.Equal(renewed.CurrentPeriodEnd, secondPeriod.PeriodEnd);

        var cancelled = await ActAsync(tenant, subscription.Id, cancel: true);
        Assert.Equal(SubscriptionStatus.Cancelled, cancelled!.Status);

        // Cancel writes no new history — the period count is unchanged after ending.
        var afterCancel = await GetAsync(tenant, subscription.Id);
        Assert.Equal(2, afterCancel!.Renewals.Count);
    }

    [Fact]
    public async Task Setup_copies_the_orders_instrument_and_renewal_charges_it_off_session()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        // The order paid with a stored instrument — the Payment row carries the provider refs.
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            db.Payments.Add(new Payment
            {
                Id = Guid.CreateVersion7(),
                OrderId = orderId,
                PaymentIntentId = $"pi_seed_{orderId:N}",
                AmountMinor = 1500,
                Currency = "EUR",
                Status = PaymentStatus.Succeeded,
                ProviderCustomerId = "cus_sub_1",
                ProviderPaymentMethodId = "pm_sub_1",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await fixture.PublishAsync(new SubscriptionRequested(
            tenant, orderId, "buyer@example.com", product, null, BillingPeriod.Monthly, 1500, "EUR"));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        Subscription? subscription = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            subscription = (await ListAsync(tenant)).SingleOrDefault(s => s.OrderId == orderId);
            if (subscription is not null)
            {
                break;
            }

            await Task.Delay(300);
        }

        Assert.NotNull(subscription);
        Assert.Equal("cus_sub_1", subscription!.ProviderCustomerId);
        Assert.Equal("pm_sub_1", subscription.ProviderPaymentMethodId);

        // Renewal charges the stored instrument off-session (mock succeeds) and advances — not PastDue.
        var renewed = await ActAsync(tenant, subscription.Id, cancel: false);
        Assert.Equal(SubscriptionStatus.Active, renewed!.Status);
    }
}
