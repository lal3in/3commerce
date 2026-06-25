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

        // Renew charges the next period via the rail (Fake) and advances.
        var renewed = await ActAsync(tenant, subscription.Id, cancel: false);
        Assert.Equal(SubscriptionStatus.Active, renewed!.Status);
        Assert.True(renewed.CurrentPeriodEnd > firstPeriodEnd);

        var cancelled = await ActAsync(tenant, subscription.Id, cancel: true);
        Assert.Equal(SubscriptionStatus.Cancelled, cancelled!.Status);
    }
}
