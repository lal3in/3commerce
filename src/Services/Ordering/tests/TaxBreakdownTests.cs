using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// ADR-0063 P1: the tax seam returns a per-line breakdown, and today's single-rate storefront logic moved into
/// <see cref="StorefrontRateTaxStrategy"/> unchanged. Two promises are pinned here:
/// <list type="number">
/// <item><b>Behaviour-preserving</b> — the strategy's total equals what checkout computed inline before P1
/// (<see cref="LegacyCheckoutTax"/> is a verbatim copy of that code, double arithmetic and all), so the charged
/// gross cannot move;</item>
/// <item><b>The parts are the whole</b> — Σ line tax + shipping tax == the total, by largest remainder, on every
/// shape (inclusive/exclusive, discounts, ship-rule exemptions, free shipping, zero value, ISO exponents 0/2/3).</item>
/// </list>
/// </summary>
public class TaxBreakdownTests
{
    // ---- 1. arithmetic identical to the old inline checkout ------------------------------------------------

    [Fact]
    public void Inclusive_and_exclusive_rounding_equals_the_old_inline_double_arithmetic_over_a_sweep()
    {
        // Checkout used `(long)Math.Round(base * bps / (10000.0 + bps), AwayFromZero)` (inclusive) and
        // `/ 10000.0` (exclusive) in DOUBLE. The strategy uses decimal (no floating point, AGENTS.md); the two agree
        // because every exact .5 is representable and every non-.5 quotient is ≥ 1/(2·(10000+bps)) away from one.
        // Swept over every rate step Catalog allows a tenant to type (0..10000) at a spread of bases.
        long[] bases = [0, 1, 2, 3, 5, 7, 11, 19, 21, 99, 100, 101, 105, 499, 999, 1_000, 1_049, 1_100, 2_099, 4_999,
            10_499, 12_345, 99_999, 1_000_000, 123_456_789, 9_999_999_999];
        for (var bps = 0; bps <= 10_000; bps += 7)
        {
            foreach (var b in bases)
            {
                Assert.Equal(LegacyInclusive(b, bps), StorefrontRateTaxStrategy.InclusiveTaxOn(b, bps));
                Assert.Equal(LegacyExclusive(b, bps), StorefrontRateTaxStrategy.ExclusiveTaxOn(b, bps));
            }
        }

        foreach (var bps in new[] { 500, 825, 1_000, 1_500, 2_000, 2_100, 2_500, 10_000 })
        {
            for (long b = 0; b <= 20_000; b++)
            {
                Assert.Equal(LegacyInclusive(b, bps), StorefrontRateTaxStrategy.InclusiveTaxOn(b, bps));
                Assert.Equal(LegacyExclusive(b, bps), StorefrontRateTaxStrategy.ExclusiveTaxOn(b, bps));
            }
        }
    }

    [Fact]
    public void Exact_halves_round_away_from_zero_as_checkout_and_RecurringChargeTax_do()
    {
        Assert.Equal(1, StorefrontRateTaxStrategy.ExclusiveTaxOn(5, 1_000));         // 0.5 → 1 (not banker's 0)
        Assert.Equal(3, StorefrontRateTaxStrategy.ExclusiveTaxOn(25, 1_000));        // 2.5 → 3
        Assert.Equal(525, StorefrontRateTaxStrategy.ExclusiveTaxOn(10_499, 500));    // 524.95 → 525
        Assert.Equal(1, StorefrontRateTaxStrategy.InclusiveTaxOn(11, 1_000));        // 11/11 = 1
        Assert.Equal(1, StorefrontRateTaxStrategy.InclusiveTaxOn(1, 10_000));        // 0.5 → 1
        Assert.Equal(2_100, StorefrontRateTaxStrategy.InclusiveTaxOn(10_499, 2_500)); // 2099.8 → 2100
    }

    [Fact]
    public void Random_carts_tax_exactly_what_the_old_inline_checkout_did_and_the_parts_sum_to_the_total()
    {
        // Property test (seeded, so a failure reproduces): random carts of 1–6 lines with random prices,
        // quantities, ship-rule exemptions, promotion + storefront-wide discount allocations (including the
        // over-allocated corner where a line's two discounts exceed its value), shipping on or off, every regime.
        var random = new Random(20_261_027);
        for (var run = 0; run < 20_000; run++)
        {
            var count = random.Next(1, 7);
            var lines = new List<TaxLineInput>();
            var promo = new long[count];
            var store = new long[count];
            var exempt = new bool[count];
            for (var i = 0; i < count; i++)
            {
                var amount = random.Next(0, 4) == 0 ? random.Next(0, 3) : random.NextInt64(1, 500_000) * random.Next(1, 4);
                promo[i] = random.Next(0, 3) == 0 ? random.NextInt64(0, amount + 1) : 0;
                store[i] = random.Next(0, 3) == 0 ? random.NextInt64(0, amount + 1) : 0;
                exempt[i] = random.Next(0, 4) == 0;
                lines.Add(new TaxLineInput(amount, promo[i] + store[i],
                    exempt[i] ? TaxExemptionReason.DestinationShipRule : TaxExemptionReason.None));
            }

            var shipping = random.Next(0, 3) == 0 ? 0 : random.NextInt64(0, 5_000);
            var bps = random.Next(0, 5) == 0 ? 0 : random.Next(1, 10_001);
            var inclusive = random.Next(0, 2) == 0;

            var breakdown = new StorefrontRateTaxStrategy(bps, inclusive)
                .TaxFor(new TaxCalculationInput("AUD", "AU", lines, shipping));

            var expected = LegacyCheckoutTax(lines.Select(l => l.AmountMinor).ToArray(), promo, store, exempt, shipping, bps, inclusive);
            Assert.Equal(expected, breakdown.TaxMinor);
            AssertWellFormed(breakdown, lines, shipping);
        }
    }

    // ---- 2. the parts ------------------------------------------------------------------------------------

    [Fact]
    public void An_exclusive_cart_splits_its_one_rounded_total_over_lines_and_shipping_by_largest_remainder()
    {
        // 5 % on (10000 + 499) = 524.95 → 525. Shares: 10000/10499 × 525 = 500.04…, 499/10499 × 525 = 24.95…
        // → 500 + 24 = 524, and the leftover unit goes to the larger remainder (shipping, .95).
        var breakdown = new StorefrontRateTaxStrategy(500, taxInclusive: false)
            .TaxFor(new TaxCalculationInput("USD", "US", [new TaxLineInput(10_000, 0)], ShippingMinor: 499));

        Assert.Equal(525, breakdown.TaxMinor);
        Assert.Equal(new LineTax(TaxCategory.Standard, 500, 10_000, 500), breakdown.Lines[0]);
        Assert.Equal(new LineTax(TaxCategory.Standard, 500, 499, 25), breakdown.Shipping);
        Assert.False(breakdown.TaxInclusive);
        Assert.Equal(TaxScheme.Domestic, breakdown.Scheme);
    }

    [Fact]
    public void An_inclusive_cart_reports_net_bases_so_base_plus_tax_is_what_the_shopper_paid()
    {
        // 25 % inclusive on 10499: tax 2100 (StorefrontTaxScopingTests' number). Goods 10000 carry 2000.19… → 2000,
        // shipping 499 carries 99.80… → 100 (larger remainder). Net bases: 8000 and 399.
        var breakdown = new StorefrontRateTaxStrategy(2_500, taxInclusive: true)
            .TaxFor(new TaxCalculationInput("EUR", "DE", [new TaxLineInput(10_000, 0)], ShippingMinor: 499));

        Assert.Equal(2_100, breakdown.TaxMinor);
        Assert.Equal(new LineTax(TaxCategory.Standard, 2_500, 8_000, 2_000), breakdown.Lines[0]);
        Assert.Equal(new LineTax(TaxCategory.Standard, 2_500, 399, 100), breakdown.Shipping);
        Assert.True(breakdown.TaxInclusive);
        Assert.Equal(10_499, breakdown.Lines[0].TaxableBaseMinor + breakdown.Lines[0].TaxMinor
            + breakdown.Shipping.TaxableBaseMinor + breakdown.Shipping.TaxMinor);
    }

    [Fact]
    public void Remainder_ties_go_to_the_earliest_line_so_the_split_is_deterministic()
    {
        // 10 % exclusive on 3 × 333 = 999 → 99.9 → 100. Each line's exact share is 33.33…: 33 each, and the one
        // leftover unit goes to the FIRST line (equal remainders, earliest wins).
        var breakdown = new StorefrontRateTaxStrategy(1_000, taxInclusive: false).TaxFor(new TaxCalculationInput(
            "AUD", "AU", [new TaxLineInput(333, 0), new TaxLineInput(333, 0), new TaxLineInput(333, 0)]));

        Assert.Equal([34L, 33L, 33L], breakdown.Lines.Select(l => l.TaxMinor));
        Assert.Equal(100, breakdown.TaxMinor);
        Assert.Equal(0, breakdown.Shipping.TaxMinor);
    }

    [Fact]
    public void Per_line_rounding_would_drift_from_the_charged_total_which_is_why_the_split_is_largest_remainder()
    {
        // Negative control for the rounding rule: rounding each line on its own gives 3 × round(33.3) = 99, a unit
        // short of the 100 actually charged on 999. The breakdown must never do that.
        var naive = 3 * StorefrontRateTaxStrategy.ExclusiveTaxOn(333, 1_000);
        var breakdown = new StorefrontRateTaxStrategy(1_000, taxInclusive: false).TaxFor(new TaxCalculationInput(
            "AUD", "AU", [new TaxLineInput(333, 0), new TaxLineInput(333, 0), new TaxLineInput(333, 0)]));

        Assert.Equal(99, naive);
        Assert.Equal(100, breakdown.Lines.Sum(l => l.TaxMinor) + breakdown.Shipping.TaxMinor);
    }

    [Fact]
    public void A_ship_rule_exempt_line_is_category_exempt_with_its_reason_and_carries_no_tax()
    {
        // 10 % exclusive. Line A (taxable) 6000 − 600 discount; line B (exempt for this destination) 4000 − 400.
        // Base = 5400 + 499 shipping → 589.9 → 590, all of it on A and shipping.
        var breakdown = new StorefrontRateTaxStrategy(1_000, taxInclusive: false).TaxFor(new TaxCalculationInput(
            "AUD", "NZ",
            [new TaxLineInput(6_000, 600), new TaxLineInput(4_000, 400, TaxExemptionReason.DestinationShipRule)],
            ShippingMinor: 499));

        Assert.Equal(590, breakdown.TaxMinor);
        Assert.Equal(TaxCategory.Standard, breakdown.Lines[0].Category);
        Assert.Equal(5_400, breakdown.Lines[0].TaxableBaseMinor);
        Assert.Equal(new LineTax(TaxCategory.Exempt, 0, 3_600, 0, TaxExemptionReason.DestinationShipRule), breakdown.Lines[1]);
        Assert.Equal(590, breakdown.Lines[0].TaxMinor + breakdown.Shipping.TaxMinor);
    }

    [Fact]
    public void A_fully_exempt_cart_does_not_tax_its_shipping_either_and_says_why()
    {
        var breakdown = new StorefrontRateTaxStrategy(1_000, taxInclusive: false).TaxFor(new TaxCalculationInput(
            "AUD", "NZ", [new TaxLineInput(4_000, 0, TaxExemptionReason.DestinationShipRule)], ShippingMinor: 499));

        Assert.Equal(0, breakdown.TaxMinor);
        Assert.Equal(new LineTax(TaxCategory.Exempt, 0, 499, 0, TaxExemptionReason.ShippingFollowsExemptGoods), breakdown.Shipping);
    }

    [Fact]
    public void Fully_discounted_goods_still_tax_the_shipping_and_put_all_of_it_on_the_shipping_line()
    {
        // A 100 % promotion leaves a goods base of 0; shipping is still taxable because the goods are.
        var breakdown = new StorefrontRateTaxStrategy(1_000, taxInclusive: false).TaxFor(new TaxCalculationInput(
            "AUD", "AU", [new TaxLineInput(5_000, 5_000)], ShippingMinor: 1_000));

        Assert.Equal(100, breakdown.TaxMinor);
        Assert.Equal(new LineTax(TaxCategory.Standard, 1_000, 0, 0), breakdown.Lines[0]);
        Assert.Equal(new LineTax(TaxCategory.Standard, 1_000, 1_000, 100), breakdown.Shipping);
    }

    [Fact]
    public void A_line_discounted_past_its_value_spills_onto_the_others_exactly_as_the_aggregate_clamp_did()
    {
        // Line A 1000 with 1500 of discount (stacked promotion + storefront share); line B 1000 with none. The old
        // code clamped the TOTAL taxable discount (1500 ≤ 2000) → goods base 500, so B's share of the base is 500,
        // not 1000. Exclusive 10 % → 50.
        var breakdown = new StorefrontRateTaxStrategy(1_000, taxInclusive: false).TaxFor(new TaxCalculationInput(
            "AUD", "AU", [new TaxLineInput(1_000, 1_500), new TaxLineInput(1_000, 0)]));

        Assert.Equal(50, breakdown.TaxMinor);
        Assert.Equal(500, breakdown.Lines.Sum(l => l.TaxableBaseMinor));
        Assert.Equal(50, breakdown.Lines.Sum(l => l.TaxMinor));
        Assert.All(breakdown.Lines, l => Assert.True(l.TaxableBaseMinor >= 0 && l.TaxMinor >= 0));
    }

    [Fact]
    public void A_zero_value_order_has_zero_facts_on_every_component()
    {
        var breakdown = new StorefrontRateTaxStrategy(1_000, taxInclusive: true).TaxFor(new TaxCalculationInput(
            "AUD", "AU", [new TaxLineInput(0, 0), new TaxLineInput(500, 500)]));

        Assert.Equal(0, breakdown.TaxMinor);
        Assert.All(breakdown.Lines, l => Assert.Equal(new LineTax(TaxCategory.Standard, 1_000, 0, 0), l));
        Assert.Equal(new LineTax(TaxCategory.Standard, 1_000, 0, 0), breakdown.Shipping);
    }

    [Fact]
    public void No_projected_storefront_keeps_the_historical_no_tax_fallback()
    {
        var strategy = StorefrontRateTaxStrategy.For(null);
        var breakdown = strategy.TaxFor(new TaxCalculationInput("AUD", "AU", [new TaxLineInput(10_000, 0)], 499));

        Assert.Equal(0, strategy.RateBasisPoints);
        Assert.False(strategy.TaxInclusive);
        Assert.Equal(0, breakdown.TaxMinor);
        Assert.Equal(10_000, breakdown.Lines[0].TaxableBaseMinor);
    }

    [Fact]
    public void For_reads_rate_and_regime_from_the_storefronts_own_copy()
    {
        var strategy = StorefrontRateTaxStrategy.For(new StorefrontTaxCopy
        {
            StorefrontId = Guid.NewGuid(),
            Currency = "AUD",
            TaxRateBasisPoints = 1_000,
            TaxInclusive = true,
            IsLive = true,
        });

        Assert.Equal(1_000, strategy.RateBasisPoints);
        Assert.True(strategy.TaxInclusive);
    }

    [Fact]
    public void The_rate_summary_is_the_sum_of_its_components_by_category_and_rate()
    {
        var breakdown = new StorefrontRateTaxStrategy(1_000, taxInclusive: true).TaxFor(new TaxCalculationInput(
            "AUD", "AU",
            [new TaxLineInput(1_100, 0), new TaxLineInput(2_200, 0), new TaxLineInput(500, 0, TaxExemptionReason.DestinationShipRule)],
            ShippingMinor: 1_100));

        Assert.Equal(
            [
                new TaxRateSummary(TaxCategory.Standard, 1_000, 4_000, 400),
                new TaxRateSummary(TaxCategory.Exempt, 0, 500, 0),
            ],
            breakdown.RateSummary);
    }

    // ---- 3. ISO 4217 exponents 0 / 2 / 3 -----------------------------------------------------------------

    [Theory]
    [InlineData("JPY")] // exponent 0: amounts are whole yen
    [InlineData("AUD")] // exponent 2: cents
    [InlineData("BHD")] // exponent 3: fils
    public void The_arithmetic_is_in_minor_units_whatever_the_currency_exponent(string currency)
    {
        // The strategy never scales by the exponent: it rounds to ONE MINOR UNIT of whatever the currency is. So the
        // same minor-unit cart gives the same facts in yen, cents or fils — 10 % inclusive on 1980 + 1650 + 1000
        // shipping = 4630 → 420.9… → 421, split 180 / 150 / 91 (the leftover unit to the largest remainder).
        var breakdown = new StorefrontRateTaxStrategy(1_000, taxInclusive: true).TaxFor(new TaxCalculationInput(
            currency, null, [new TaxLineInput(1_980, 0), new TaxLineInput(1_650, 0)], ShippingMinor: 1_000));

        Assert.Equal(421, breakdown.TaxMinor);
        Assert.Equal([180L, 150L], breakdown.Lines.Select(l => l.TaxMinor));
        Assert.Equal(91, breakdown.Shipping.TaxMinor);
        Assert.Equal(4_630, breakdown.Lines.Sum(l => l.TaxableBaseMinor + l.TaxMinor)
            + breakdown.Shipping.TaxableBaseMinor + breakdown.Shipping.TaxMinor);
    }

    [Fact]
    public void A_three_decimal_currency_rounds_half_away_from_zero_at_the_fils()
    {
        // BHD 12.345 + 1.500 shipping at 10 % exclusive = 1.3845 BHD → 1.385 (1385 fils).
        var breakdown = new StorefrontRateTaxStrategy(1_000, taxInclusive: false).TaxFor(new TaxCalculationInput(
            "BHD", "BH", [new TaxLineInput(12_345, 0)], ShippingMinor: 1_500));

        Assert.Equal(1_385, breakdown.TaxMinor);
        Assert.Equal(1_385, breakdown.Lines[0].TaxMinor + breakdown.Shipping.TaxMinor);
    }

    [Fact]
    public void A_zero_decimal_currency_rounds_half_away_from_zero_at_the_yen()
    {
        // JPY 105 at 10 % exclusive = 10.5 → 11 yen (banker's rounding would give 10).
        var breakdown = new StorefrontRateTaxStrategy(1_000, taxInclusive: false).TaxFor(new TaxCalculationInput(
            "JPY", "JP", [new TaxLineInput(105, 0)]));

        Assert.Equal(11, breakdown.TaxMinor);
    }

    // ---- 4. the pricing engine and the order carry the breakdown -------------------------------------------

    [Fact]
    public void The_pricing_engine_exposes_a_breakdown_whose_parts_sum_to_its_tax()
    {
        var engine = new PricingEngine(new HomeRegimeTaxStrategy("AU", rateBasisPoints: 1_000, pricesIncludeTax: true));
        var random = new Random(7);
        for (var run = 0; run < 2_000; run++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 5)).Select(_ => new PricingLineInput(
                Guid.NewGuid(), null, null, 0, random.NextInt64(0, 50_000), random.Next(1, 4),
                random.Next(0, 4) == 0 ? TaxMode.Exempt : TaxMode.Inclusive)).ToList();
            var result = engine.Price(
                new PricingInput(Guid.NewGuid(), Guid.NewGuid(), "AUD", lines, 0, ShipCountry: "AU", StorefrontDiscountBps: random.Next(0, 3_000)),
                []);

            Assert.NotNull(result.Tax);
            Assert.Equal(result.TaxMinor, result.Tax!.Lines.Sum(l => l.TaxMinor) + result.Tax.Shipping.TaxMinor);
            Assert.All(result.Tax.Lines.Where((_, i) => lines[i].TaxMode == TaxMode.Exempt),
                l => Assert.Equal(TaxExemptionReason.ProductTaxMode, l.ExemptionReason));
        }
    }

    [Fact]
    public void An_attempt_hands_its_tax_facts_to_the_order_verbatim()
    {
        var attempt = new CheckoutAttempt
        {
            Id = Guid.CreateVersion7(),
            Email = "a@example.com",
            Currency = "AUD",
            PaymentIntentId = "pi_1",
            ShipName = "N",
            ShipLine1 = "L",
            ShipCity = "C",
            ShipPostcode = "P",
            ShipCountry = "AU",
            TaxMinor = 421,
            ShippingTaxCategory = TaxCategory.Standard,
            ShippingTaxRateBps = 1_000,
            ShippingTaxableBaseMinor = 909,
            ShippingTaxMinor = 91,
            TaxScheme = TaxScheme.Domestic,
            TaxInclusive = true,
            Lines =
            [
                new CheckoutAttemptLine
                {
                    Title = "A", Quantity = 1, UnitPriceMinor = 1_980,
                    TaxCategory = TaxCategory.Standard, TaxRateBps = 1_000, TaxableBaseMinor = 1_800, TaxMinor = 180,
                },
                new CheckoutAttemptLine
                {
                    Title = "B", Quantity = 1, UnitPriceMinor = 1_650,
                    TaxCategory = TaxCategory.Exempt, TaxableBaseMinor = 1_650, ExemptionReason = TaxExemptionReason.DestinationShipRule,
                },
            ],
        };

        var order = attempt.ToOrder(1_000, DateTimeOffset.UtcNow);

        Assert.Equal((TaxCategory.Standard, 1_000, 909L, 91L, TaxExemptionReason.None),
            (order.ShippingTaxCategory, order.ShippingTaxRateBps, order.ShippingTaxableBaseMinor, order.ShippingTaxMinor, order.ShippingExemptionReason));
        Assert.Equal(TaxScheme.Domestic, order.TaxScheme);
        Assert.True(order.TaxInclusive);
        Assert.Equal(
            [(TaxCategory.Standard, 1_000, 1_800L, 180L, TaxExemptionReason.None), (TaxCategory.Exempt, 0, 1_650L, 0L, TaxExemptionReason.DestinationShipRule)],
            order.Lines.Select(l => (l.TaxCategory, l.TaxRateBps, l.TaxableBaseMinor, l.TaxMinor, l.ExemptionReason)));
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>The structural invariants every breakdown must satisfy.</summary>
    internal static void AssertWellFormed(TaxBreakdown breakdown, IReadOnlyList<TaxLineInput> lines, long shippingMinor)
    {
        Assert.Equal(lines.Count, breakdown.Lines.Count);
        Assert.Equal(breakdown.TaxMinor, breakdown.Lines.Sum(l => l.TaxMinor) + breakdown.Shipping.TaxMinor);
        Assert.Equal(breakdown.TaxMinor, breakdown.RateSummary.Sum(r => r.TaxMinor));
        Assert.All(breakdown.Lines, l => Assert.True(l.TaxMinor >= 0 && l.TaxableBaseMinor >= 0, $"negative fact {l}"));
        Assert.True(breakdown.Shipping.TaxMinor >= 0 && breakdown.Shipping.TaxableBaseMinor >= 0);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Exemption != TaxExemptionReason.None)
            {
                Assert.Equal(TaxCategory.Exempt, breakdown.Lines[i].Category);
                Assert.Equal(0, breakdown.Lines[i].TaxMinor);
            }
        }

        // The taxable components' bases (gross of tax on an inclusive store) are exactly the base that was taxed.
        var taxable = breakdown.Lines.Where(l => l.Category == TaxCategory.Standard).Append(breakdown.Shipping)
            .Where(c => c.Category == TaxCategory.Standard)
            .Sum(c => breakdown.TaxInclusive ? c.TaxableBaseMinor + c.TaxMinor : c.TaxableBaseMinor);
        var taxableGoods = lines.Where(l => l.Exemption == TaxExemptionReason.None).Sum(l => l.AmountMinor);
        var taxableDiscount = Math.Min(lines.Where(l => l.Exemption == TaxExemptionReason.None).Sum(l => l.DiscountMinor), taxableGoods);
        Assert.Equal(taxableGoods - taxableDiscount + (taxableGoods > 0 ? shippingMinor : 0), taxable);
    }

    /// <summary>
    /// A VERBATIM copy of the tax CheckoutEndpoints computed inline before ADR-0063 P1 (develop @ c637bd4), double
    /// arithmetic included — the reference the strategy must reproduce to the minor unit.
    /// </summary>
    private static long LegacyCheckoutTax(
        long[] lineTotals, long[] promotionLineDiscounts, long[] storefrontLineDiscounts, bool[] exempt,
        long shippingMinor, int taxBps, bool taxInclusive)
    {
        long taxableSubtotal = 0;
        for (var i = 0; i < lineTotals.Length; i++)
        {
            if (!exempt[i])
            {
                taxableSubtotal += lineTotals[i];
            }
        }

        var taxableShippingMinor = taxableSubtotal > 0 ? shippingMinor : 0;
        long taxableDiscountMinor = 0;
        for (var i = 0; i < lineTotals.Length; i++)
        {
            if (exempt[i])
            {
                continue;
            }

            taxableDiscountMinor += promotionLineDiscounts[i] + storefrontLineDiscounts[i];
        }

        taxableDiscountMinor = Math.Min(taxableDiscountMinor, taxableSubtotal);
        var taxBaseMinor = taxableSubtotal - taxableDiscountMinor + taxableShippingMinor;
        return taxInclusive
            ? (long)Math.Round(taxBaseMinor * taxBps / (10000.0 + taxBps), MidpointRounding.AwayFromZero)
            : (long)Math.Round(taxBaseMinor * taxBps / 10000.0, MidpointRounding.AwayFromZero);
    }

    private static long LegacyInclusive(long taxBaseMinor, int taxBps) =>
        (long)Math.Round(taxBaseMinor * taxBps / (10000.0 + taxBps), MidpointRounding.AwayFromZero);

    private static long LegacyExclusive(long taxBaseMinor, int taxBps) =>
        (long)Math.Round(taxBaseMinor * taxBps / 10000.0, MidpointRounding.AwayFromZero);
}
