using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// Chargeback (dispute) postings (phase 2). A chargeback reverses the sale like a refund — revenue,
/// shipping and tax back out through the store's own accounts (or the shared contra accounts), gross
/// out of cash.{provider} — and books the provider's dispute fee to its own chargeback-fee account,
/// distinct from processing fees so the P&amp;L can surface disputes separately.
/// </summary>
public class ChargebackLedgerTests
{
    private static long Debits(JournalEntry entry, string account) =>
        entry.Lines.Where(l => l.AccountCode == account).Sum(l => l.DebitMinor);

    private static long Credits(JournalEntry entry, string account) =>
        entry.Lines.Where(l => l.AccountCode == account).Sum(l => l.CreditMinor);

    private static void AssertBalanced(JournalEntry entry) =>
        Assert.Equal(entry.Lines.Sum(l => l.DebitMinor), entry.Lines.Sum(l => l.CreditMinor));

    [Fact]
    public void A_chargeback_reverses_the_sale_and_books_the_dispute_fee_shared_accounts()
    {
        var orderId = Guid.CreateVersion7();
        // gross 10000, tax 900, shipping 500 → net revenue 8600; dispute fee 1500.
        var entry = Ledger.Chargeback(orderId, 10000, 900, 1500, "AUD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, "stripe", shippingMinor: 500);

        Assert.Equal($"Chargeback for order {orderId} via Card", entry.Description);
        Assert.Equal(8600, Debits(entry, Accounts.RevenueRefunds));
        Assert.Equal(500, Debits(entry, Accounts.ShippingIncome));
        Assert.Equal(900, Debits(entry, Accounts.LiabilityTaxCollected));
        // Cash out = disputed gross (10000) + dispute fee (1500).
        Assert.Equal(11500, Credits(entry, Accounts.CashFor("stripe")));
        Assert.Equal(1500, Debits(entry, Accounts.ChargebackFeesFor("stripe")));
        AssertBalanced(entry);
    }

    [Fact]
    public void A_storefront_chargeback_reverses_the_stores_own_revenue_tax_and_shipping()
    {
        var storeId = Guid.CreateVersion7();
        var rev = $"revenue.store-{storeId:N}";
        var tax = $"tax.store-{storeId:N}";
        var ship = $"shipping.store-{storeId:N}";
        var recv = $"receivable.store-{storeId:N}";
        var orderId = Guid.CreateVersion7();

        var entry = Ledger.Chargeback(orderId, 25081, 4183, 1500, "EUR", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, "stripe", rev, tax, recv, shippingMinor: 4574, shippingAccount: ship);

        Assert.Equal(25081 - 4183 - 4574, Debits(entry, rev)); // net revenue reversed to the store's account
        Assert.Equal(4574, Debits(entry, ship));
        Assert.Equal(4183, Debits(entry, tax));
        Assert.Equal(0, Debits(entry, Accounts.RevenueRefunds)); // shared contra untouched when attributed
        Assert.Equal(1500, Debits(entry, Accounts.ChargebackFeesFor("stripe")));
        AssertBalanced(entry);
    }

    [Fact]
    public void A_chargeback_without_a_fee_still_reverses_the_sale()
    {
        var orderId = Guid.CreateVersion7();
        var entry = Ledger.Chargeback(orderId, 5000, 0, 0, "USD", DateTimeOffset.UtcNow, PaymentMethodKind.Card, "stripe");

        Assert.Equal(5000, Debits(entry, Accounts.RevenueRefunds));
        Assert.Equal(5000, Credits(entry, Accounts.CashFor("stripe")));
        Assert.Equal(0, Debits(entry, Accounts.ChargebackFeesFor("stripe")));
        AssertBalanced(entry);
    }
}
