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
}
