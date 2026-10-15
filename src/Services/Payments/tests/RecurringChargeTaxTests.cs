using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// renewal_usage_tax_2026_10_11 (ADR-0063 P4): a subscription renewal or a metered usage charge posts the tax a
/// checkout of the same amount in the same storefront would have posted — before this both posted tax 0, so on an
/// inclusive (AU GST / EU VAT) store the whole charge landed in revenue and output tax was under-reported. The
/// split is checkout's (ADR-0038): inclusive extracts <c>amount × bps / (10000 + bps)</c>, half away from zero,
/// and the charged gross never changes; exclusive ADDS <c>price × bps / 10000</c> (half away from zero) and the
/// charge is price + tax, as checkout charges the first period (owner decision 2026-10-15, option A).
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
        // Applied to renewals/usage since the owner decision of 2026-10-15 (option A): swept like the inclusive split.
        foreach (var bps in new[] { 400, 625, 725, 825, 1000, 2000 })
        {
            for (long price = 0; price <= 20_000; price++)
            {
                Assert.Equal(CheckoutExclusiveTax(price, bps), RecurringChargeTax.ExclusiveTaxOn(price, bps));
                Assert.Equal(CheckoutExclusiveTax(price, bps), RecurringChargeTax.For(price, bps, taxInclusive: false).TaxMinor);
            }
        }

        Assert.Equal(1, RecurringChargeTax.ExclusiveTaxOn(5, 1000)); // 0.5 → away from zero
    }

    [Theory]
    [InlineData(1000, 825, 83, 1083)]   // US 8.25%: $10.00 + 82.5¢ → 83¢ (away from zero) → $10.83
    [InlineData(999, 825, 82, 1081)]    // 82.4175 → 82
    [InlineData(2000, 1000, 200, 2200)]
    [InlineData(5, 1000, 1, 6)]         // 0.5 → 1
    [InlineData(4, 1000, 0, 4)]         // a sub-unit tax rounds to nothing → charged the bare price, no tax line
    public void An_exclusive_charge_adds_checkouts_tax_on_top_of_the_price(long price, int bps, long expectedTax, long expectedCharge)
    {
        // Owner decision 2026-10-15 (option A): a renewal / usage charge on a US-style store is charged price + tax,
        // exactly as checkout charged the first period; the price stays the net revenue.
        var result = RecurringChargeTax.For(price, bps, taxInclusive: false);

        Assert.Equal(RecurringChargeTaxBasis.Exclusive, result.Basis);
        Assert.Equal(expectedTax, result.TaxMinor);
        Assert.Equal(expectedCharge, result.ChargeMinor);
        Assert.Equal(price, result.NetMinor);
    }

    [Theory]
    [InlineData(2200, 1000)]
    [InlineData(1999, 2100)]
    public void An_inclusive_charge_is_the_price_unchanged(long price, int bps)
    {
        var result = RecurringChargeTax.For(price, bps, taxInclusive: true);

        Assert.Equal(price, result.ChargeMinor);                 // the shelf price IS the charge
        Assert.Equal(price - result.TaxMinor, result.NetMinor);
    }

    [Theory]
    [InlineData(0, false, true)]     // a 0% inclusive store
    [InlineData(0, false, false)]    // a 0% exclusive store: nothing to add
    [InlineData(1000, true, true)]   // a destination-exempt line (ProductShipRule)
    [InlineData(825, true, false)]   // a destination-exempt line on an exclusive store: no tax ADDED either
    public void A_zero_rate_or_exempt_line_is_untaxed_and_charged_the_price(int bps, bool exempt, bool inclusive)
    {
        var result = RecurringChargeTax.For(2200, bps, taxInclusive: inclusive, taxExempt: exempt);

        Assert.Equal(RecurringChargeTaxBasis.Untaxed, result.Basis);
        Assert.Equal(0, result.TaxMinor);
        Assert.Equal(2200, result.ChargeMinor);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_unprojected_rate_is_unknown_never_guessed_and_charges_the_bare_price(bool inclusive)
    {
        // The rate is not projected (a pre-P4 projection, or no storefront): never a guessed rate, never a refused
        // renewal — the bare price is charged and no tax posted (an exclusive store under-collects; it is logged).
        var result = RecurringChargeTax.For(2200, null, taxInclusive: inclusive);

        Assert.Equal(RecurringChargeTaxBasis.Unknown, result.Basis);
        Assert.Equal(0, result.TaxMinor);
        Assert.Equal(2200, result.ChargeMinor);
    }

    [Fact]
    public void A_zero_decimal_currency_rounds_to_whole_units()
    {
        // JPY has no minor unit: minor = yen. ¥1,000 at 10% inclusive contains ¥90.909… → ¥91.
        Assert.Equal(91, RecurringChargeTax.For(1000, 1000, taxInclusive: true).TaxMinor);
        Assert.Equal(100, RecurringChargeTax.For(1100, 1000, taxInclusive: true).TaxMinor);
        // Exclusive: ¥999 at 8.25% adds ¥82.4175 → ¥82, charged ¥1,081; ¥1,000 adds ¥82.5 → ¥83 (away from zero).
        Assert.Equal(1081, RecurringChargeTax.For(999, 825, taxInclusive: false).ChargeMinor);
        Assert.Equal(1083, RecurringChargeTax.For(1000, 825, taxInclusive: false).ChargeMinor);
    }

    [Theory]
    [InlineData("USD", 1000, 825, 83)]
    [InlineData("CAD", 1999, 1300, 260)]   // 259.87 → 260
    [InlineData("JPY", 999, 825, 82)]
    public void An_exclusive_sale_books_the_price_as_net_and_the_added_tax_with_the_charge_as_gross(
        string currency, long price, int bps, long expectedTax)
    {
        var tax = RecurringChargeTax.For(price, bps, taxInclusive: false);
        var entry = Ledger.Sale(Guid.CreateVersion7(), tax.ChargeMinor, tax.TaxMinor, 0, currency, DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, "stripe", Rev, Tax, Recv, 0, Ship, Sid, "renew-u-2", "renewal");

        Assert.Equal(price, Credits(entry, Rev));                                         // net revenue = the price
        Assert.Equal(expectedTax, Credits(entry, Tax));                                   // the added tax
        Assert.Equal(price + expectedTax, Debits(entry, Accounts.CashStoreFor(Sid, "stripe"))); // what was charged
        Assert.Equal(entry.Lines.Sum(l => l.DebitMinor), entry.Lines.Sum(l => l.CreditMinor));
        Assert.All(entry.Lines, l => Assert.Equal(currency, l.Currency));
        Assert.All(entry.Lines, l => Assert.True((l.DebitMinor > 0) ^ (l.CreditMinor > 0))); // ck_line_one_side
        Assert.DoesNotContain(entry.Lines, l => Accounts.IsSharedCode(l.AccountCode));
    }

    [Fact]
    public void A_full_refund_of_an_exclusive_renewal_shaped_sale_reverses_net_and_tax_exactly()
    {
        var tax = RecurringChargeTax.For(1000, 825, taxInclusive: false);
        var sale = Ledger.Sale(Guid.CreateVersion7(), tax.ChargeMinor, tax.TaxMinor, 0, "USD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, "stripe", Rev, Tax, Recv, 0, Ship, Sid, "renew-v-2", "renewal");
        var refund = Ledger.Refund(Guid.CreateVersion7(), Guid.CreateVersion7(), tax.ChargeMinor, tax.TaxMinor, "USD", DateTimeOffset.UtcNow,
            PaymentMethodKind.Card, "stripe", Rev, Tax, Recv, 0, Ship, Sid);

        foreach (var account in sale.Lines.Concat(refund.Lines).Select(l => l.AccountCode).Distinct())
        {
            Assert.Equal(0, Debits(sale, account) - Credits(sale, account) + Debits(refund, account) - Credits(refund, account));
        }
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
