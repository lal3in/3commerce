using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Usage.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// mt7_5 closing flow: the period-close sweep bills unbilled overage and rolls every balance whose window
/// has ended (counters reset), while balances whose window is still open are left untouched.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class UsagePeriodCloseTests(Phase4Fixture fixture)
{
    private async Task<T> WithServiceAsync<T>(Func<UsageService, Task<T>> act)
    {
        using var scope = fixture.Usage.Services.CreateScope();
        return await act(scope.ServiceProvider.GetRequiredService<UsageService>());
    }

    [Fact]
    public async Task Close_due_periods_rolls_ended_balances_and_leaves_open_ones()
    {
        var tenant = Guid.NewGuid();
        const string dueEmail = "due@example.com";
        const string openEmail = "open@example.com";

        // A balance whose window has already ended, with overage on the clock → due for close.
        await WithServiceAsync(async svc =>
        {
            await svc.ProvisionAsync(tenant, dueEmail, MeterType.Token, 100, overageAllowed: true, overageUnitPriceMinor: 5,
                "AUD", periodEnd: DateTimeOffset.UtcNow.AddMinutes(-1), default);
            return await svc.RecordAsync(tenant, dueEmail, MeterType.Token, 130, reference: null, default);
        });

        // A balance whose window is still open → must be left alone.
        await WithServiceAsync(async svc =>
        {
            await svc.ProvisionAsync(tenant, openEmail, MeterType.Token, 100, overageAllowed: true, overageUnitPriceMinor: 5,
                "AUD", periodEnd: DateTimeOffset.UtcNow.AddDays(30), default);
            return await svc.RecordAsync(tenant, openEmail, MeterType.Token, 50, reference: null, default);
        });

        var closed = await WithServiceAsync(svc => svc.CloseDuePeriodsAsync(default));
        Assert.True(closed >= 1);

        var dueBalance = (await WithServiceAsync(svc => svc.ListBalancesAsync(tenant, dueEmail, default))).Single();
        Assert.Equal(0, dueBalance.UsedQuantity);            // rolled: counters reset
        Assert.Equal(0, dueBalance.UnbilledOverageQuantity); // overage was billed before the roll

        var openBalance = (await WithServiceAsync(svc => svc.ListBalancesAsync(tenant, openEmail, default))).Single();
        Assert.Equal(50, openBalance.UsedQuantity);          // untouched — window still open
    }
}
