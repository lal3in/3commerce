using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Per-store order cost postings (phase 1). Carrier-label cost accrual is the first of these:
/// buying a label debits the shipping-cost expense and credits liability.carrier_payable — no real
/// money moves (the carrier invoices later), mirroring SupplierPayable's accrual shape. Later
/// batches extend this file with COGS accrual / RMA-disposition posting tests.
/// </summary>
public class OrderCostLedgerTests
{
    private static long Debits(JournalEntry entry, string account) =>
        entry.Lines.Where(l => l.AccountCode == account).Sum(l => l.DebitMinor);

    private static long Credits(JournalEntry entry, string account) =>
        entry.Lines.Where(l => l.AccountCode == account).Sum(l => l.CreditMinor);

    private static void AssertBalanced(JournalEntry entry) =>
        Assert.Equal(entry.Lines.Sum(l => l.DebitMinor), entry.Lines.Sum(l => l.CreditMinor));

    [Fact]
    public void A_carrier_cost_books_to_the_shared_shipping_carrier_account_when_unattributed()
    {
        var packageId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        var entry = Ledger.CarrierCost(packageId, orderId, 650, "AUD", DateTimeOffset.UtcNow);

        Assert.Equal($"Carrier label for order {orderId}", entry.Description);
        Assert.Equal(packageId.ToString(), entry.Reference);
        Assert.Equal(650, Debits(entry, Accounts.ExpenseShippingCarrier));
        Assert.Equal(650, Credits(entry, Accounts.LiabilityCarrierPayable));
        AssertBalanced(entry);
    }

    [Fact]
    public void A_carrier_cost_books_to_the_stores_own_shipping_cost_account_when_attributed()
    {
        var storeId = Guid.CreateVersion7();
        var packageId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var storeAccount = Accounts.ShippingCostStoreFor(storeId);

        var entry = Ledger.CarrierCost(packageId, orderId, 800, "AUD", DateTimeOffset.UtcNow, storeAccount);

        Assert.Equal(800, Debits(entry, storeAccount));
        Assert.Equal(0, Debits(entry, Accounts.ExpenseShippingCarrier)); // shared fallback untouched
        Assert.Equal(800, Credits(entry, Accounts.LiabilityCarrierPayable));
        AssertBalanced(entry);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Zero_or_negative_carrier_cost_produces_no_entry(long costMinor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Ledger.CarrierCost(Guid.CreateVersion7(), Guid.CreateVersion7(), costMinor, "AUD", DateTimeOffset.UtcNow));
    }

    private static SupplierPayablePolicy Policy(int commissionBps)
    {
        var tenantId = Guid.CreateVersion7();
        var supplierId = Guid.CreateVersion7();
        return new SupplierPayablePolicy
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SupplierEntityId = supplierId,
            CommissionBps = commissionBps,
            Cadence = PayoutCadence.Weekly,
        };
    }

    [Fact]
    public void A_cogs_accrual_books_to_the_stores_own_account_when_attributed()
    {
        var storeId = Guid.CreateVersion7();
        var policy = Policy(commissionBps: 0);
        var payable = SupplierPayable.Create(
            policy.TenantId, policy.SupplierEntityId, Guid.CreateVersion7(), 12_000, "AUD", policy, DateTimeOffset.UtcNow);

        var entry = payable.ToAccrualEntry(DateTimeOffset.UtcNow, Accounts.CogsStoreFor(storeId));

        Assert.Equal(12_000, Debits(entry, Accounts.CogsStoreFor(storeId)));
        Assert.Equal(0, Debits(entry, Accounts.ExpenseCostOfGoodsSold)); // shared fallback untouched when attributed
        Assert.Equal(12_000, Credits(entry, Accounts.LiabilitySupplierPayable));
        AssertBalanced(entry);
    }

    [Fact]
    public void A_cogs_accrual_falls_back_to_the_shared_account_when_unattributed()
    {
        var policy = Policy(commissionBps: 0);
        var payable = SupplierPayable.Create(
            policy.TenantId, policy.SupplierEntityId, Guid.CreateVersion7(), 5_000, "AUD", policy, DateTimeOffset.UtcNow);

        // No cogs account (null / blank) keeps the original shared-fallback behaviour identical.
        var entry = payable.ToAccrualEntry(DateTimeOffset.UtcNow);
        var blankEntry = payable.ToAccrualEntry(DateTimeOffset.UtcNow, "   ");

        Assert.Equal(5_000, Debits(entry, Accounts.ExpenseCostOfGoodsSold));
        Assert.Equal(5_000, Debits(blankEntry, Accounts.ExpenseCostOfGoodsSold));
        Assert.Equal(5_000, Credits(entry, Accounts.LiabilitySupplierPayable));
        AssertBalanced(entry);
    }

    [Fact]
    public void A_cogs_accrual_books_the_supplier_net_after_commission_to_the_store_account()
    {
        // A 2000 bps (20%) commission means the platform only owes the supplier its 80% cut, so the COGS
        // accrual debits the NET — matching the existing ToAccrualEntry semantics (SupplierPayableTests).
        var storeId = Guid.CreateVersion7();
        var policy = Policy(commissionBps: 2_000);
        var payable = SupplierPayable.Create(
            policy.TenantId, policy.SupplierEntityId, Guid.CreateVersion7(), 10_000, "AUD", policy, DateTimeOffset.UtcNow);

        Assert.Equal(8_000, payable.NetPayableMinor);

        var entry = payable.ToAccrualEntry(DateTimeOffset.UtcNow, Accounts.CogsStoreFor(storeId));

        Assert.Equal(8_000, Debits(entry, Accounts.CogsStoreFor(storeId)));
        Assert.Equal(8_000, Credits(entry, Accounts.LiabilitySupplierPayable));
        AssertBalanced(entry);
    }
}
