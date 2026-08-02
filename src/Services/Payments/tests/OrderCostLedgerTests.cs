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

    [Fact]
    public void A_restock_reversal_books_to_the_stores_own_cogs_account_when_attributed()
    {
        // Restock returns goods to sellable stock; the COGS accrual is reversed so a resale doesn't
        // double-expense: Dr liability.supplier_payable / Cr the store's own expense.cogs.store-{id}.
        var storeId = Guid.CreateVersion7();
        var rmaId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var cogs = Accounts.CogsStoreFor(storeId);

        var entry = Ledger.CogsReversal(rmaId, revision: 1, orderId, 12_000, "AUD", DateTimeOffset.UtcNow, cogs);

        Assert.Equal($"{rmaId}:1", entry.Reference);
        Assert.Equal(12_000, Debits(entry, Accounts.LiabilitySupplierPayable));
        Assert.Equal(12_000, Credits(entry, cogs));
        Assert.Equal(0, Credits(entry, Accounts.CostOfGoodsSold)); // shared fallback untouched when attributed
        AssertBalanced(entry);
    }

    [Fact]
    public void A_restock_reversal_falls_back_to_the_shared_cogs_account_when_unattributed()
    {
        var rmaId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        var entry = Ledger.CogsReversal(rmaId, revision: 1, orderId, 5_000, "AUD", DateTimeOffset.UtcNow);
        var blank = Ledger.CogsReversal(rmaId, revision: 1, orderId, 5_000, "AUD", DateTimeOffset.UtcNow, "   ");

        Assert.Equal(5_000, Debits(entry, Accounts.LiabilitySupplierPayable));
        Assert.Equal(5_000, Credits(entry, Accounts.CostOfGoodsSold));
        Assert.Equal(5_000, Credits(blank, Accounts.CostOfGoodsSold));
        AssertBalanced(entry);
    }

    [Fact]
    public void A_storage_writeoff_reclasses_cogs_to_the_stores_own_writeoff_account_when_attributed()
    {
        // Storage keeps the goods off sale; the accrued COGS is reclassed to a write-off so the loss is
        // its own P&L line: Dr expense.writeoffs.store-{id} / Cr expense.cogs.store-{id}. Total expense
        // is unchanged (one expense account credited, another debited).
        var storeId = Guid.CreateVersion7();
        var rmaId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var cogs = Accounts.CogsStoreFor(storeId);
        var writeoff = Accounts.WriteoffsStoreFor(storeId);

        var entry = Ledger.Writeoff(rmaId, revision: 1, orderId, 8_000, "AUD", DateTimeOffset.UtcNow, cogs, writeoff);

        Assert.Equal($"{rmaId}:1", entry.Reference);
        Assert.Equal(8_000, Debits(entry, writeoff));
        Assert.Equal(8_000, Credits(entry, cogs));
        Assert.Equal(0, Debits(entry, Accounts.ExpenseWriteoffs)); // shared fallback untouched when attributed
        AssertBalanced(entry);
    }

    [Fact]
    public void A_storage_writeoff_falls_back_to_the_shared_accounts_when_unattributed()
    {
        var rmaId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();

        var entry = Ledger.Writeoff(rmaId, revision: 1, orderId, 3_000, "AUD", DateTimeOffset.UtcNow);

        Assert.Equal(3_000, Debits(entry, Accounts.ExpenseWriteoffs));
        Assert.Equal(3_000, Credits(entry, Accounts.CostOfGoodsSold));
        AssertBalanced(entry);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Non_positive_disposition_amounts_produce_no_entry(long costMinor)
    {
        var rmaId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Ledger.CogsReversal(rmaId, 1, orderId, costMinor, "AUD", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Ledger.Writeoff(rmaId, 1, orderId, costMinor, "AUD", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void An_edit_correction_reverses_the_previous_revisions_posting_line_for_line()
    {
        // A disposition edited Storage → Restock reverses the previous (Storage) posting before applying
        // the new (Restock) one. The reversing entry swaps every line of the prior entry and balances on
        // its own; the pair leaves the books reflecting only the current disposition.
        var storeId = Guid.CreateVersion7();
        var rmaId = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var cogs = Accounts.CogsStoreFor(storeId);
        var writeoff = Accounts.WriteoffsStoreFor(storeId);

        var storage = Ledger.Writeoff(rmaId, revision: 1, orderId, 8_000, "AUD", DateTimeOffset.UtcNow, cogs, writeoff);
        var reversal = Ledger.ReverseOf(storage, $"{rmaId}:2:reversal", DateTimeOffset.UtcNow);

        Assert.Equal($"{rmaId}:2:reversal", reversal.Reference);
        // Prior Dr writeoff / Cr cogs becomes Cr writeoff / Dr cogs.
        Assert.Equal(8_000, Credits(reversal, writeoff));
        Assert.Equal(8_000, Debits(reversal, cogs));
        AssertBalanced(reversal);

        // Net of the original + its reversal is zero on every account.
        var restock = Ledger.CogsReversal(rmaId, revision: 2, orderId, 8_000, "AUD", DateTimeOffset.UtcNow, cogs);
        var all = new[] { storage, reversal, restock };
        Assert.Equal(all.Sum(e => e.Lines.Sum(l => l.DebitMinor)), all.Sum(e => e.Lines.Sum(l => l.CreditMinor)));
        // The write-off account nets to zero (storage posted it, the reversal undid it); the Restock stands.
        Assert.Equal(
            all.Sum(e => Debits(e, writeoff)),
            all.Sum(e => Credits(e, writeoff)));
        Assert.Equal(8_000, Debits(restock, Accounts.LiabilitySupplierPayable));
    }
}
