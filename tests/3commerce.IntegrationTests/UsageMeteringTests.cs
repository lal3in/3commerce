using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt7_4: provision an allowance, record usage (idempotent + incremental), read the balance.</summary>
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

        await WithUsageAsync(s => s.ProvisionAsync(tenant, email, MeterType.Token, 1000, null, default));
        await WithUsageAsync(s => s.RecordAsync(tenant, email, MeterType.Token, 250, "evt-1", default));
        await WithUsageAsync(s => s.RecordAsync(tenant, email, MeterType.Token, 250, "evt-1", default)); // duplicate reference
        var balance = await WithUsageAsync(s => s.RecordAsync(tenant, email, MeterType.Token, 100, "evt-2", default));

        Assert.Equal(350, balance.UsedQuantity); // 250 + 100; the duplicate was ignored
        Assert.Equal(650, balance.RemainingQuantity);

        var balances = await WithUsageAsync(s => s.ListBalancesAsync(tenant, email, default));
        Assert.Single(balances); // one balance per (tenant, customer, meter)
    }

    [Fact]
    public async Task Usage_past_the_allowance_is_overage()
    {
        var tenant = Guid.NewGuid();
        const string email = "heavy@example.com";

        await WithUsageAsync(s => s.ProvisionAsync(tenant, email, MeterType.Request, 100, null, default));
        var balance = await WithUsageAsync(s => s.RecordAsync(tenant, email, MeterType.Request, 150, "r-1", default));

        Assert.Equal(0, balance.RemainingQuantity);
        Assert.Equal(50, balance.OverageQuantity);
    }
}
