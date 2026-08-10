using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// ledger_sf_4 — the no-shared-account invariant. Every storefront-attributed posting must land entirely
/// on the store's own accounts; no line may reference a shared/default code (there is no platform/tenant
/// default — a movement is always attributed to a specific storefront). This locks the invariant across
/// every ledger factory so a future change that reintroduces a shared fallback on an attributed path fails
/// here. Unattributed (null-storefront, no-store-account) postings still use the shared codes forward-only,
/// which is asserted separately in OrderCostLedgerTests/LedgerAttributionTests.
/// </summary>
public class NoSharedAccountPostingTests
{
    private static readonly Guid Sid = Guid.Parse("5c0be000-0000-0000-0000-0000000000aa");
    private const string Prov = "stripe";

    private static string Rev => $"revenue.store-{Sid:N}";
    private static string Tax => $"tax.store-{Sid:N}";
    private static string Recv => $"receivable.store-{Sid:N}";
    private static string Ship => $"shipping.store-{Sid:N}";

    private static void AssertNoSharedLine(JournalEntry entry)
    {
        var shared = entry.Lines.Where(l => Accounts.IsSharedCode(l.AccountCode)).Select(l => l.AccountCode).Distinct().ToList();
        Assert.True(shared.Count == 0, $"attributed entry posted to shared account(s): {string.Join(", ", shared)}");
        Assert.NotEmpty(entry.Lines); // a real movement, not a vacuous pass
    }

    [Fact]
    public void IsSharedCode_classifies_shared_vs_store_codes()
    {
        Assert.True(Accounts.IsSharedCode(Accounts.RevenueSales));
        Assert.True(Accounts.IsSharedCode(Accounts.LiabilitySupplierPayable));
        Assert.True(Accounts.IsSharedCode(Accounts.CashFor(Prov)));
        Assert.True(Accounts.IsSharedCode(Accounts.ChargebackFeesFor("polar")));

        Assert.False(Accounts.IsSharedCode(Accounts.CashStoreFor(Sid, Prov)));
        Assert.False(Accounts.IsSharedCode(Accounts.SupplierPayableStoreFor(Sid)));
        Assert.False(Accounts.IsSharedCode(Accounts.CarrierPayableStoreFor(Sid)));
        Assert.False(Accounts.IsSharedCode(Rev));
    }

    [Fact]
    public void An_attributed_sale_posts_no_shared_line() =>
        AssertNoSharedLine(Ledger.Sale(Guid.CreateVersion7(), 10000, 900, 300, "AUD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, Prov, Rev, Tax, Recv, shippingMinor: 500, shippingAccount: Ship, storefrontId: Sid));

    [Fact]
    public void An_attributed_refund_posts_no_shared_line() =>
        AssertNoSharedLine(Ledger.Refund(Guid.CreateVersion7(), Guid.CreateVersion7(), 10000, 900, "AUD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, Prov, Rev, Tax, Recv, shippingMinor: 500, shippingAccount: Ship, storefrontId: Sid));

    [Fact]
    public void An_attributed_chargeback_posts_no_shared_line() =>
        AssertNoSharedLine(Ledger.Chargeback(Guid.CreateVersion7(), 10000, 900, 1500, "AUD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, Prov, Rev, Tax, Recv, shippingMinor: 500, shippingAccount: Ship, storefrontId: Sid));

    [Fact]
    public void An_attributed_carrier_cost_posts_no_shared_line() =>
        AssertNoSharedLine(Ledger.CarrierCost(Guid.CreateVersion7(), Guid.CreateVersion7(), 650, "AUD", DateTimeOffset.UtcNow,
            Accounts.ShippingCostStoreFor(Sid), Accounts.CarrierPayableStoreFor(Sid)));

    [Fact]
    public void An_attributed_cogs_accrual_posts_no_shared_line()
    {
        var policy = new SupplierPayablePolicy
        {
            Id = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            SupplierEntityId = Guid.CreateVersion7(),
            CommissionBps = 0,
            Cadence = PayoutCadence.Weekly,
        };
        var payable = SupplierPayable.Create(policy.TenantId, policy.SupplierEntityId, Guid.CreateVersion7(), 12_000, "AUD", policy, DateTimeOffset.UtcNow);
        AssertNoSharedLine(payable.ToAccrualEntry(DateTimeOffset.UtcNow, Accounts.CogsStoreFor(Sid), Accounts.SupplierPayableStoreFor(Sid)));
    }

    [Fact]
    public void An_attributed_restock_reversal_posts_no_shared_line() =>
        AssertNoSharedLine(Ledger.CogsReversal(Guid.CreateVersion7(), 1, Guid.CreateVersion7(), 12_000, "AUD", DateTimeOffset.UtcNow,
            Accounts.CogsStoreFor(Sid), Accounts.SupplierPayableStoreFor(Sid)));

    [Fact]
    public void An_attributed_storage_writeoff_posts_no_shared_line() =>
        AssertNoSharedLine(Ledger.Writeoff(Guid.CreateVersion7(), 1, Guid.CreateVersion7(), 8_000, "AUD", DateTimeOffset.UtcNow,
            Accounts.CogsStoreFor(Sid), Accounts.WriteoffsStoreFor(Sid)));
}
