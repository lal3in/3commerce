using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt7_4/mt7_5: provision, record (idempotent + incremental), gate access, bill overage.</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class UsageMeteringTests(Phase4Fixture fixture)
{
    private async Task<T> WithUsageAsync<T>(Func<UsageService, Task<T>> work)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<UsageService>());
    }

    [Fact]
    public async Task Records_roll_into_the_balance_incrementally_and_idempotently()
    {
        var tenant = Guid.NewGuid();
        const string email = "buyer@example.com";

        await WithUsageAsync(s => s.ProvisionAsync(tenant, email, MeterType.Token, 1000, true, 1, "AUD", null, default));
        await WithUsageAsync(s => s.RecordAsync(tenant, email, MeterType.Token, 250, "evt-1", default));
        await WithUsageAsync(s => s.RecordAsync(tenant, email, MeterType.Token, 250, "evt-1", default)); // duplicate reference
        var balance = await WithUsageAsync(s => s.RecordAsync(tenant, email, MeterType.Token, 100, "evt-2", default));

        Assert.Equal(350, balance.UsedQuantity); // 250 + 100; the duplicate was ignored
        Assert.Equal(650, balance.RemainingQuantity);

        var balances = await WithUsageAsync(s => s.ListBalancesAsync(tenant, email, default));
        Assert.Single(balances); // one balance per (tenant, customer, meter)
    }

    [Fact]
    public async Task Usage_past_the_allowance_is_rejected_when_overage_not_allowed()
    {
        var tenant = Guid.NewGuid();
        const string email = "capped@example.com";

        await WithUsageAsync(s => s.ProvisionAsync(tenant, email, MeterType.Request, 100, false, 0, "AUD", null, default));
        await WithUsageAsync(s => s.RecordAsync(tenant, email, MeterType.Request, 80, "r-1", default));

        await Assert.ThrowsAsync<FulfillmentRuleException>(
            () => WithUsageAsync(s => s.RecordAsync(tenant, email, MeterType.Request, 50, "r-2", default)));
    }

    [Fact]
    public async Task Overage_is_rated_billed_once_then_cleared()
    {
        var tenant = Guid.NewGuid();
        const string email = "heavy@example.com";

        var provisioned = await WithUsageAsync(s => s.ProvisionAsync(tenant, email, MeterType.Request, 100, true, 5, "AUD", null, default));
        await WithUsageAsync(s => s.RecordAsync(tenant, email, MeterType.Request, 150, "u-1", default)); // 50 over

        var billed = await WithUsageAsync(s => s.BillOverageAsync(tenant, provisioned.Id, default));
        Assert.Equal(50, billed!.OverageQuantity);
        Assert.Equal(0, billed.UnbilledOverageChargeMinor); // marked billed (50 × 5 was charged)

        // Re-billing without new usage is a no-op.
        var rebilled = await WithUsageAsync(s => s.BillOverageAsync(tenant, provisioned.Id, default));
        Assert.Equal(0, rebilled!.UnbilledOverageChargeMinor);
    }
}
