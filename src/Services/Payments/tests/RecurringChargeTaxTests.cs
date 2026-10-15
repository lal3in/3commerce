using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// renewal_usage_tax_2026_10_11 (ADR-0063 P4): a subscription renewal or a metered usage charge posts the tax a
/// checkout of the same amount in the same storefront would have posted — before this both posted tax 0, so on an
/// inclusive (AU GST / EU VAT) store the whole charge landed in revenue and output tax was under-reported. The
/// split is checkout's (ADR-0038): inclusive extracts <c>amount × bps / (10000 + bps)</c>, half away from zero,
/// and the charged gross never changes. Exclusive regimes post no tax pending the owner's decision.
/// </summary>
public class RecurringChargeTaxTests
{
    private static readonly Guid Sid = Guid.Parse("3f2a0000-0000-0000-0000-0000000000cc");
    private static string Rev => $"revenue.store-{Sid:N}";
    private static string Tax => $"tax.store-{Sid:N}";
    private static string Recv => $"receivable.store-{Sid:N}";
    private static string Ship => $"shipping.store-{Sid:N}";

    private static long Credits(JournalEntry e, string a) => e.Lines.Where(l => l.AccountCode == a).Sum(l => l.CreditMinor);
    private static long Debits(JournalEntry e, string a) => e.Lines.Where(l => l.AccountCode == a).Sum(l => l.DebitMinor);

    // CheckoutEndpoints' inclusive extraction, verbatim (the reference the renewal must equal).
    private static long CheckoutInclusiveTax(long taxBaseMinor, int taxBps) =>
        (long)Math.Round(taxBaseMinor * taxBps / (10000.0 + taxBps), MidpointRounding.AwayFromZero);

    private static long CheckoutExclusiveTax(long taxBaseMinor, int taxBps) =>
        (long)Math.Round(taxBaseMinor * taxBps / 10000.0, MidpointRounding.AwayFromZero);

    [Theory]
    [InlineData(2200, 1000, 200)]   // AU GST 10%: A$22.00 contains A$2.00
    [InlineData(1100, 1000, 100)]
    [InlineData(1999, 2100, 347)]   // NL VAT 21%: 1999 × 2100 / 12100 = 346.94 → 347
    [InlineData(1200, 2000, 200)]   // 20% VAT: 12.00 contains 2.00
    [InlineData(3, 2000, 1)]        // 0.5 exactly → away from zero (banker's would give 0)
    [InlineData(9, 2000, 2)]        // 1.5 → 2
    [InlineData(1, 1000, 0)]        // a sub-unit tax rounds to nothing → no tax line
    public void An_inclusive_charge_extracts_the_contained_tax(long charge, int bps, long expectedTax)
    {
        var result = RecurringChargeTax.For(charge, bps, taxInclusive: true);

        Assert.Equal(RecurringChargeTaxBasis.Inclusive, result.Basis);
        Assert.Equal(expectedTax, result.TaxMinor);
    }

    [Fact]
    public void The_inclusive_split_equals_checkouts_for_every_amount_and_common_rate()
    {
        // "The same tax a checkout would have charged on the same amount in the same storefront" — swept, so a
        // rounding drift between the two implementations (decimal here, double at checkout) cannot hide.
        foreach (var bps in new[] { 500, 700, 1000, 1500, 1900, 2000, 2100, 2500, 2700 })
        {
            for (long charge = 0; charge <= 20_000; charge++)
            {
                Assert.Equal(CheckoutInclusiveTax(charge, bps), RecurringChargeTax.For(charge, bps, taxInclusive: true).TaxMinor);
            }
        }
    }

    [Fact]
    public void The_exclusive_add_on_equals_checkouts_too()
    {
        // Not applied to renewals/usage today (owner decision pending) — pinned so the decision is a call-site change.
        foreach (var bps in new[] { 400, 625, 725, 1000, 2000 })
        {
            for (long price = 0; price <= 20_000; price += 7)
            {
                Assert.Equal(CheckoutExclusiveTax(price, bps), RecurringChargeTax.ExclusiveTaxOn(price, bps));
            }
        }

        Assert.Equal(1, RecurringChargeTax.ExclusiveTaxOn(5, 1000)); // 0.5 → away from zero
    }

    [Fact]
    public void An_exclusive_charge_posts_no_tax_and_says_why()
    {
        // The charge omits the tax checkout would add; posting a split would book tax nobody collected.
        var result = RecurringChargeTax.For(1000, 825, taxInclusive: false);

        Assert.Equal(RecurringChargeTaxBasis.ExclusiveNotCharged, result.Basis);
        Assert.Equal(0, result.TaxMinor);
    }

    [Theory]
    [InlineData(0, false)]    // a 0% store
    [InlineData(1000, true)]  // a destination-exempt line (ProductShipRule)
    public void A_zero_rate_or_exempt_line_is_untaxed(int bps, bool exempt)
    {
        var result = RecurringChargeTax.For(2200, bps, taxInclusive: true, taxExempt: exempt);

        Assert.Equal(RecurringChargeTaxBasis.Untaxed, result.Basis);
        Assert.Equal(0, result.TaxMinor);
    }

    [Fact]
    public void An_unprojected_rate_is_unknown_never_guessed()
    {
        var result = RecurringChargeTax.For(2200, null, taxInclusive: true);

        Assert.Equal(RecurringChargeTaxBasis.Unknown, result.Basis);
        Assert.Equal(0, result.TaxMinor);
    }

    [Fact]
    public void A_zero_decimal_currency_rounds_to_whole_units()
    {
        // JPY has no minor unit: minor = yen. ¥1,000 at 10% inclusive contains ¥90.909… → ¥91.
        Assert.Equal(91, RecurringChargeTax.For(1000, 1000, taxInclusive: true).TaxMinor);
        Assert.Equal(100, RecurringChargeTax.For(1100, 1000, taxInclusive: true).TaxMinor);
    }

    [Theory]
    [InlineData("AUD", 2200, 1000, 200)]
    [InlineData("EUR", 1999, 2100, 347)]
    [InlineData("JPY", 1000, 1000, 91)]
    public void A_renewal_sale_books_net_and_tax_to_the_stores_accounts_with_the_gross_unchanged(
        string currency, long charge, int bps, long expectedTax)
    {
        var tax = RecurringChargeTax.For(charge, bps, taxInclusive: true);
        var entry = Ledger.Sale(Guid.CreateVersion7(), charge, tax.TaxMinor, 0, currency, DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, "stripe", Rev, Tax, Recv, 0, Ship, Sid, "renew-x-2", "renewal");

        Assert.Equal(charge - expectedTax, Credits(entry, Rev));             // net revenue
        Assert.Equal(expectedTax, Credits(entry, Tax));                      // output tax to the store's tax account
        Assert.Equal(charge, Debits(entry, Accounts.CashStoreFor(Sid, "stripe"))); // gross collected, unchanged
        Assert.Equal(entry.Lines.Sum(l => l.DebitMinor), entry.Lines.Sum(l => l.CreditMinor));
        Assert.All(entry.Lines, l => Assert.Equal(currency, l.Currency));
        Assert.All(entry.Lines, l => Assert.True((l.DebitMinor > 0) ^ (l.CreditMinor > 0))); // ck_line_one_side
        Assert.DoesNotContain(entry.Lines, l => Accounts.IsSharedCode(l.AccountCode));
    }

    [Fact]
    public void A_zero_rate_renewal_posts_no_tax_line()
    {
        var tax = RecurringChargeTax.For(1500, 0, taxInclusive: true);
        var entry = Ledger.Sale(Guid.CreateVersion7(), 1500, tax.TaxMinor, 0, "EUR", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, "stripe", Rev, Tax, Recv, 0, Ship, Sid, "renew-y-2", "renewal");

        Assert.DoesNotContain(entry.Lines, l => l.AccountCode == Tax); // never a zero line (ck_line_one_side)
        Assert.Equal(1500, Credits(entry, Rev));
    }

    [Fact]
    public void A_full_refund_of_a_renewal_shaped_sale_reverses_net_and_tax_exactly()
    {
        // The refund contract a renewal refund must honour once one exists: Ledger.Refund with the sale's tax
        // portion nets every account the sale touched (revenue, tax, receivable, cash) back to zero.
        var tax = RecurringChargeTax.For(2200, 1000, taxInclusive: true);
        var sale = Ledger.Sale(Guid.CreateVersion7(), 2200, tax.TaxMinor, 0, "AUD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, "stripe", Rev, Tax, Recv, 0, Ship, Sid, "renew-z-2", "renewal");
        var refund = Ledger.Refund(Guid.CreateVersion7(), Guid.CreateVersion7(), 2200, tax.TaxMinor, "AUD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, "stripe", Rev, Tax, Recv, 0, Ship, Sid);

        foreach (var account in sale.Lines.Concat(refund.Lines).Select(l => l.AccountCode).Distinct())
        {
            var net = Debits(sale, account) - Credits(sale, account) + Debits(refund, account) - Credits(refund, account);
            Assert.Equal(0, net);
        }
    }
}
