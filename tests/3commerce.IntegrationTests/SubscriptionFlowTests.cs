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
}
