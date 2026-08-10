using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// jobmgr_2: the auto-renew sweep renews due subscriptions per storefront — only for storefronts whose daily
/// time has passed and are enabled, at most once per storefront per day (off-session).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class SubscriptionAutoRenewTests(Phase4Fixture fixture)
{
    private async Task<Guid> SeedDueSubscriptionAsync(Guid tenant, Guid storefront)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var sub = await service.StartAsync(new SubscriptionRequested(
            tenant, Guid.NewGuid(), "buyer@example.com", Guid.NewGuid(), null, BillingPeriod.Monthly, 1500, "EUR", storefront), default);
        // Backdate the period so the sweep sees it as due (Start always opens a future period).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE payments.\"Subscriptions\" SET \"CurrentPeriodEnd\" = {DateTimeOffset.UtcNow.AddDays(-1)} WHERE \"Id\" = {sub.Id}");
        return sub.Id;
    }

    private async Task SeedScheduleAsync(Guid storefront, bool enabled)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var schedule = StorefrontBillingSchedule.CreateDefault(storefront, DateTimeOffset.UtcNow);
        schedule.Configure(new TimeOnly(0, 0, 0), enabled, DateTimeOffset.UtcNow); // midnight → already-passed today
        db.StorefrontBillingSchedules.Add(schedule);
        await db.SaveChangesAsync();
    }

    private async Task<Subscription> GetAsync(Guid id)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.Subscriptions.Include(s => s.Renewals).SingleAsync(s => s.Id == id);
    }

    private async Task<int> SweepAsync()
    {
        using var scope = fixture.Payments.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SubscriptionService>().AutoRenewDueAsync(default);
    }

    private async Task SeedLedgerAccountsAsync(Guid storefront)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        db.StorefrontLedgerAccounts.Add(new StorefrontLedgerAccounts
        {
            StorefrontId = storefront,
            RevenueAccountCode = $"revenue.store-{storefront:N}",
            TaxAccountCode = $"tax.store-{storefront:N}",
            ReceivableAccountCode = $"receivable.store-{storefront:N}",
            ShippingAccountCode = $"shipping.store-{storefront:N}",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sweep_posts_renewal_revenue_to_the_storefronts_own_ledger_accounts()
    {
        var tenant = Guid.NewGuid();
        var store = Guid.NewGuid();
        await SeedLedgerAccountsAsync(store);
        var subId = await SeedDueSubscriptionAsync(tenant, store); // price 1500 EUR
        await SeedScheduleAsync(store, enabled: true);

        Assert.Equal(1, await SweepAsync());

        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        // The renewal booked its own entry (reference renew-{id}-{sequence}), distinct from any order sale.
        var entry = await db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Reference == $"renew-{subId}-2");
        var revenue = entry.Lines.Where(l => l.AccountCode == $"revenue.store-{store:N}").Sum(l => l.CreditMinor);
        var cash = entry.Lines.Where(l => l.AccountCode == Accounts.CashStoreFor(store, "stripe")).Sum(l => l.DebitMinor);
        Assert.Equal(1500, revenue); // revenue recorded to the store's own account (was: nothing posted)
        Assert.Equal(1500, cash);    // settled into the store's own cash account
        Assert.DoesNotContain(entry.Lines, l => Accounts.IsSharedCode(l.AccountCode)); // no shared/default line
        Assert.Equal(entry.Lines.Sum(l => l.DebitMinor), entry.Lines.Sum(l => l.CreditMinor)); // balanced
    }

    [Fact]
    public async Task Sweep_renews_due_subscriptions_in_enabled_storefronts_only_once_per_day()
    {
        var tenant = Guid.NewGuid();
        var enabledStore = Guid.NewGuid();
        var disabledStore = Guid.NewGuid();

        var enabledSub = await SeedDueSubscriptionAsync(tenant, enabledStore);
        var disabledSub = await SeedDueSubscriptionAsync(tenant, disabledStore);
        await SeedScheduleAsync(enabledStore, enabled: true);
        await SeedScheduleAsync(disabledStore, enabled: false);

        var renewed = await SweepAsync();

        Assert.Equal(1, renewed); // only the enabled storefront's due sub
        Assert.Equal(2, (await GetAsync(enabledSub)).Renewals.Count);   // renewed → second period recorded
        Assert.Single((await GetAsync(disabledSub)).Renewals);          // untouched — schedule disabled

        // Idempotent: the enabled storefront already ran today, so a second sweep renews nothing.
        Assert.Equal(0, await SweepAsync());
    }
}
