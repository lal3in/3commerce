using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Usage.Domain;
using ThreeCommerce.Usage.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// jobmgr_3 prepaid auto-load: recording usage that drives the prepaid credit to the customer's threshold
/// tops the balance up (event-driven), and the daily safety-net sweep tops up any balance left at/below it.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class UsageAutoLoadTests(Phase4Fixture fixture)
{
    private const string Email = "autoload@example.com";

    private UsageService Service(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<UsageService>();

    [Fact]
    public async Task Recording_usage_tops_up_prepaid_credit_when_it_hits_the_threshold()
    {
        var tenant = Guid.NewGuid();

        await using (var scope = fixture.Usage.Services.CreateAsyncScope())
        {
            var svc = Service(scope);
            // Unit price 5; the customer enables auto-load: top up 100 units when credit ≤ 10.
            await svc.ProvisionAsync(tenant, Email, MeterType.Token, includedQuantity: 0, overageAllowed: false,
                overageUnitPriceMinor: 5, "AUD", periodEnd: null, default);
            await svc.ConfigureAutoLoadAsync(tenant, Email, MeterType.Token, enabled: true, thresholdQuantity: 10, reloadQuantity: 100, default);

            // First usage: prepaid is 0 (≤ 10) → an auto-load fires, crediting 100.
            var balance = await svc.RecordAsync(tenant, Email, MeterType.Token, 1, reference: null, default);
            Assert.Equal(100, balance.PrepaidRemainingQuantity);
            Assert.Equal(1, balance.AutoLoadCount);
        }

        // Draw the credit down to the threshold to trigger a second top-up.
        await using (var scope = fixture.Usage.Services.CreateAsyncScope())
        {
            var svc = Service(scope);
            var balance = await svc.RecordAsync(tenant, Email, MeterType.Token, 95, reference: null, default); // 100 → 5 (≤ 10)
            Assert.Equal(105, balance.PrepaidRemainingQuantity); // topped up again (5 + 100)
            Assert.Equal(2, balance.AutoLoadCount);
        }
    }

    [Fact]
    public async Task The_daily_sweep_tops_up_a_balance_left_below_threshold()
    {
        var tenant = Guid.NewGuid();
        const string email = "sweep-autoload@example.com";

        await using var scope = fixture.Usage.Services.CreateAsyncScope();
        var svc = Service(scope);
        await svc.ProvisionAsync(tenant, email, MeterType.Token, 0, false, 5, "AUD", null, default);
        // Enable auto-load without recording — prepaid sits at 0 (≤ threshold), i.e. a top-up is owed.
        await svc.ConfigureAutoLoadAsync(tenant, email, MeterType.Token, enabled: true, thresholdQuantity: 10, reloadQuantity: 50, default);

        var toppedUp = await svc.SweepAutoLoadsAsync(default);

        Assert.True(toppedUp >= 1);
        var balance = (await svc.ListBalancesAsync(tenant, email, default)).Single();
        Assert.Equal(50, balance.PrepaidRemainingQuantity);
    }
}
