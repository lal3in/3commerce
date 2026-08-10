using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// pay_usage_charges_post_revenue: a metered overage / prepaid auto-load charge, once settled, records
/// revenue in the Payments ledger attributed to the balance's storefront — before this the consumers only
/// authorized the card and posted nothing. Published straight onto the Payments bus so the consumer runs
/// end-to-end; the ledger entry is keyed by the charge's own reference.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class UsageChargeRevenueTests(Phase4Fixture fixture)
{
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

    private Task PublishAsync(object message) =>
        // Publish on the transport directly (IBus), NOT the scoped IPublishEndpoint — the latter routes
        // through the EF transactional outbox and would need a DbContext SaveChanges to flush, so it never
        // leaves in a bare test publish. The real producer (UsageService) flushes via its own SaveChanges.
        fixture.Payments.Services.GetRequiredService<IBus>().Publish(message);

    private async Task<List<JournalLine>> PollEntryLinesAsync(string reference)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Payments.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            var entry = await db.JournalEntries.Include(e => e.Lines).AsNoTracking()
                .SingleOrDefaultAsync(e => e.Reference == reference);
            if (entry is not null)
            {
                return entry.Lines.ToList();
            }

            await Task.Delay(300);
        }

        throw new Xunit.Sdk.XunitException($"No ledger entry posted for charge {reference}.");
    }

    [Fact]
    public async Task A_settled_overage_charge_posts_revenue_to_the_storefronts_ledger_accounts()
    {
        var store = Guid.NewGuid();
        await SeedLedgerAccountsAsync(store);
        var reference = $"overage-{Guid.NewGuid():N}";

        await PublishAsync(new UsageOverageCharge(
            Guid.NewGuid(), "buyer@example.com", MeterType.Token, OverageQuantity: 20, ChargeMinor: 900, "EUR", reference, store));

        var lines = await PollEntryLinesAsync(reference);
        Assert.Equal(900, lines.Where(l => l.AccountCode == $"revenue.store-{store:N}").Sum(l => l.CreditMinor));
        Assert.Equal(900, lines.Where(l => l.AccountCode == Accounts.CashStoreFor(store, "stripe")).Sum(l => l.DebitMinor));
        Assert.DoesNotContain(lines, l => Accounts.IsSharedCode(l.AccountCode));
        Assert.Equal(lines.Sum(l => l.DebitMinor), lines.Sum(l => l.CreditMinor));
    }

    [Fact]
    public async Task A_settled_auto_load_charge_posts_revenue_to_the_storefronts_ledger_accounts()
    {
        var store = Guid.NewGuid();
        await SeedLedgerAccountsAsync(store);
        var reference = $"autoload-{Guid.NewGuid():N}";

        await PublishAsync(new UsageAutoLoadCharge(
            Guid.NewGuid(), "buyer@example.com", MeterType.Token, ReloadQuantity: 100, ChargeMinor: 500, "EUR", reference, store));

        var lines = await PollEntryLinesAsync(reference);
        Assert.Equal(500, lines.Where(l => l.AccountCode == $"revenue.store-{store:N}").Sum(l => l.CreditMinor));
        Assert.DoesNotContain(lines, l => Accounts.IsSharedCode(l.AccountCode));
        Assert.Equal(lines.Sum(l => l.DebitMinor), lines.Sum(l => l.CreditMinor));
    }
}
