using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// The ledger must attribute a posting to the method the shopper chose and the PSP that settled it,
/// instead of hard-coding Stripe. Two guarantees are load-bearing for the admin ledger page and for
/// multi-PSP reconciliation: the description always ends "via {MethodKind}", and cash/fees post to
/// <c>cash.{provider}</c> / <c>expense.{provider}_fees</c> with stripe keeping its original codes.
/// Balance is asserted alongside, because attribution must never cost us the invariant (NFR-1).
/// </summary>
public class LedgerAttributionTests
{
    private static long Debits(JournalEntry entry, string account) =>
        entry.Lines.Where(l => l.AccountCode == account).Sum(l => l.DebitMinor);

    private static long Credits(JournalEntry entry, string account) =>
        entry.Lines.Where(l => l.AccountCode == account).Sum(l => l.CreditMinor);

    private static void AssertBalanced(JournalEntry entry) =>
        Assert.Equal(entry.Lines.Sum(l => l.DebitMinor), entry.Lines.Sum(l => l.CreditMinor));

    [Fact]
    public void A_google_pay_sale_on_stripe_posts_to_cash_stripe_and_names_the_method()
    {
        var orderId = Guid.CreateVersion7();
        var entry = Ledger.Sale(orderId, 11_900, 1_900, 350, "EUR", DateTimeOffset.UtcNow, PaymentMethodKind.GooglePay, "stripe");

        Assert.Equal($"Sale for order {orderId} via GooglePay", entry.Description);
        Assert.Contains("via GooglePay", entry.Description);

        // Gross in, fee back out — both on the stripe cash account, exactly as before pay_4.
        Assert.Equal(11_900, Debits(entry, Accounts.CashStripe));
        Assert.Equal(350, Credits(entry, Accounts.CashStripe));
        Assert.Equal(350, Debits(entry, Accounts.ExpenseStripeFees));
        Assert.Equal(10_000, Credits(entry, Accounts.RevenueSales));
        Assert.Equal(1_900, Credits(entry, Accounts.LiabilityTaxCollected));
        AssertBalanced(entry);
    }

    [Theory]
    [InlineData(PaymentMethodKind.Card, "Card")]
    [InlineData(PaymentMethodKind.ApplePay, "ApplePay")]
    [InlineData(PaymentMethodKind.GooglePay, "GooglePay")]
    [InlineData(PaymentMethodKind.PayPal, "PayPal")]
    [InlineData(PaymentMethodKind.Afterpay, "Afterpay")]
    [InlineData(PaymentMethodKind.Polar, "Polar")]
    public void Every_method_kind_lands_in_the_description(PaymentMethodKind kind, string expected)
    {
        var sale = Ledger.Sale(Guid.CreateVersion7(), 5_000, 0, 0, "EUR", DateTimeOffset.UtcNow, kind, "stripe");
        var refund = Ledger.Refund(Guid.CreateVersion7(), Guid.CreateVersion7(), 5_000, 0, "EUR", DateTimeOffset.UtcNow, kind, "stripe");

        Assert.EndsWith($"via {expected}", sale.Description);
        Assert.EndsWith($"via {expected}", refund.Description);
    }

    [Theory]
    [InlineData("polar")]
    [InlineData("paypal")]
    [InlineData("afterpay")]
    public void A_sale_under_a_non_stripe_provider_posts_to_its_own_cash_and_fee_accounts(string provider)
    {
        var entry = Ledger.Sale(Guid.CreateVersion7(), 8_000, 0, 200, "EUR", DateTimeOffset.UtcNow, PaymentMethodKind.PayPal, provider);

        Assert.Equal($"cash.{provider}", Accounts.CashFor(provider));
        Assert.Equal(8_000, Debits(entry, $"cash.{provider}"));
        Assert.Equal(200, Credits(entry, $"cash.{provider}"));
        Assert.Equal(200, Debits(entry, $"expense.{provider}_fees"));

        // Nothing leaks onto Stripe's accounts any more.
        Assert.Equal(0, Debits(entry, Accounts.CashStripe));
        Assert.Equal(0, Debits(entry, Accounts.ExpenseStripeFees));
        AssertBalanced(entry);
    }

    [Fact]
    public void A_refund_returns_cash_to_the_provider_that_took_it()
    {
        var entry = Ledger.Refund(Guid.CreateVersion7(), Guid.CreateVersion7(), 5_950, 950, "EUR", DateTimeOffset.UtcNow, PaymentMethodKind.ApplePay, "paypal");

        Assert.Equal(5_950, Credits(entry, "cash.paypal"));
        Assert.Equal(0, Credits(entry, Accounts.CashStripe));
        Assert.Equal(5_000, Debits(entry, Accounts.RevenueRefunds));
        Assert.Equal(950, Debits(entry, Accounts.LiabilityTaxCollected));
        AssertBalanced(entry);
    }

    [Fact]
    public void The_default_posting_is_unchanged_stripe_card_so_legacy_rows_stay_coherent()
    {
        var entry = Ledger.Sale(Guid.CreateVersion7(), 1_000, 0, 0, "EUR", DateTimeOffset.UtcNow);

        Assert.EndsWith("via Card", entry.Description);
        Assert.Equal(1_000, Debits(entry, Accounts.CashStripe));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mock")]          // offline dev moves no real money — keep it on the seeded stripe cash
    [InlineData("not-a-psp")]     // never post to an account the chart of accounts has not seeded
    [InlineData("STRIPE")]        // provider keys are compared lowercase
    public void An_unknown_or_offline_provider_falls_back_to_the_seeded_stripe_accounts(string? provider)
    {
        Assert.Equal(Accounts.CashStripe, Accounts.CashFor(provider));
        Assert.Equal(Accounts.ExpenseStripeFees, Accounts.FeesFor(provider));
    }

    [Fact]
    public void Every_known_provider_has_a_distinct_cash_and_fee_account()
    {
        var codes = LedgerProviders.Known
            .SelectMany(p => new[] { Accounts.CashFor(p), Accounts.FeesFor(p) })
            .ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Contains(Accounts.CashStripe, codes);
        Assert.Contains(Accounts.ExpenseStripeFees, codes);
    }
}
