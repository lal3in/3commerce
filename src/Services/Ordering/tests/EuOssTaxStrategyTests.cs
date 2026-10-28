using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// ADR-0063 P1b: the EU OSS-aware tax strategy. Pinned here:
/// <list type="number">
/// <item>the EU-27 standard-rate fixture (an independent copy, so a typo in the table fails a test);</item>
/// <item>the EUR 10,000 threshold — the home rate at or under it (current AND preceding year), the destination rate
/// with scheme OSS over it, including the very sale that takes the year over;</item>
/// <item>a rate change takes effect on its date, at LOCAL midnight in the Member State;</item>
/// <item>inclusive storefronts charge the shelf price unchanged — the destination VAT is extracted, never added;</item>
/// <item>selection by the storefront's jurisdiction, and every non-EU / unconfigured storefront byte-identical to
/// <see cref="StorefrontRateTaxStrategy"/> (P1's property-test approach).</item>
/// </list>
/// </summary>
public class EuOssTaxStrategyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly EuDistanceSalesTurnover OverThreshold = new(EuOssTaxStrategy.ThresholdMinorEur + 1, 0);

    /// <summary>
    /// The EU-27 standard rates in force on 28 Oct 2026 (EC "VAT rates applied in the Member States"), typed
    /// independently of <see cref="EuVatRateTable.Current"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> StandardRates2026 = new Dictionary<string, int>
    {
        ["AT"] = 2_000,
        ["BE"] = 2_100,
        ["BG"] = 2_000,
        ["HR"] = 2_500,
        ["CY"] = 1_900,
        ["CZ"] = 2_100,
        ["DK"] = 2_500,
        ["EE"] = 2_400,
        ["FI"] = 2_550,
        ["FR"] = 2_000,
        ["DE"] = 1_900,
        ["GR"] = 2_400,
        ["HU"] = 2_700,
        ["IE"] = 2_300,
        ["IT"] = 2_200,
        ["LV"] = 2_100,
        ["LT"] = 2_100,
        ["LU"] = 1_700,
        ["MT"] = 1_800,
        ["NL"] = 2_100,
        ["PL"] = 2_300,
        ["PT"] = 2_300,
        ["RO"] = 2_100,
        ["SK"] = 2_300,
        ["SI"] = 2_200,
        ["ES"] = 2_100,
        ["SE"] = 2_500,
    };

    // ---- 1. the EU-27 fixture -----------------------------------------------------------------------------

    [Fact]
    public void The_table_has_exactly_the_27_member_states_at_their_known_standard_rates()
    {
        Assert.Equal(27, StandardRates2026.Count);
        Assert.Equal(StandardRates2026.Keys.Order(), EuMemberStates.Codes.Order());
        Assert.Equal(StandardRates2026.Keys.Order(), EuVatRateTable.Current.Rows.Select(r => r.Country).Distinct().Order());

        var onDate = DateOnly.FromDateTime(Now.UtcDateTime);
        foreach (var (country, bps) in StandardRates2026)
        {
            Assert.True(bps == EuVatRateTable.Current.StandardRateFor(country, onDate), $"{country}: expected {bps}");
        }

        // Every Member State has a rate from the day the Union OSS started; nothing is in force before it.
        Assert.All(StandardRates2026.Keys, c => Assert.NotNull(EuVatRateTable.Current.StandardRateFor(c, EuVatRateTable.OssStart)));
        Assert.All(StandardRates2026.Keys, c => Assert.Null(EuVatRateTable.Current.StandardRateFor(c, EuVatRateTable.OssStart.AddDays(-1))));
    }

    [Fact]
    public void Over_the_threshold_a_German_store_charges_every_other_member_states_own_rate_under_OSS()
    {
        // Exclusive, 10000 net, no shipping → the tax IS the rate in bps.
        foreach (var (country, bps) in StandardRates2026)
        {
            var breakdown = Strategy("DE", 1_900, inclusive: false, OverThreshold).TaxFor(Input("EUR", country, 10_000));
            var expected = country == "DE" ? (1_900, TaxScheme.Domestic) : (bps, TaxScheme.Oss);
            Assert.True(expected == (breakdown.TaxMinor, breakdown.Scheme), $"{country}: {breakdown.TaxMinor} {breakdown.Scheme}");
            Assert.Equal(expected.Item1, breakdown.Lines[0].RateBps);
            Assert.Equal(TaxCategory.Standard, breakdown.Lines[0].Category);
        }
    }

    [Fact]
    public void Every_member_state_time_zone_resolves_on_this_host()
    {
        // The local-midnight rule needs the time-zone database (the .NET runtime images ship it). Without it the
        // strategy falls back to the UTC date — this test is what keeps that fallback a fallback.
        Assert.True(EuMemberStates.AllTimeZonesResolve());
    }

    // ---- 2. the threshold -------------------------------------------------------------------------------

    [Fact]
    public void Below_the_threshold_a_cross_border_sale_charges_the_home_rate_exactly_as_the_storefront_rate_strategy()
    {
        var input = Input("EUR", "FR", 10_000, shipping: 499);
        var eu = Strategy("DE", 1_900, inclusive: true, new EuDistanceSalesTurnover(500_000, 400_000)).TaxFor(input);
        var single = new StorefrontRateTaxStrategy(1_900, taxInclusive: true).TaxFor(input);

        Assert.Equal(TaxScheme.Domestic, eu.Scheme);
        AssertSameBreakdown(single, eu);
        Assert.Equal(StorefrontRateTaxStrategy.InclusiveTaxOn(10_499, 1_900), eu.TaxMinor); // 1676
    }

    [Fact]
    public void The_threshold_is_exceeded_only_strictly_above_EUR_10000_and_the_crossing_sale_is_itself_OSS()
    {
        // Exclusive DE store; the sale is worth 10000 net. 990000 + 10000 = exactly 1000000 → not exceeded → home.
        var atThreshold = Strategy("DE", 1_900, inclusive: false, new EuDistanceSalesTurnover(0, 990_000))
            .TaxFor(Input("EUR", "FR", 10_000));
        Assert.Equal((TaxScheme.Domestic, 1_900L), (atThreshold.Scheme, atThreshold.TaxMinor));

        // One cent more in the year → this very sale takes the year over → destination (FR 20 %) under OSS.
        var crossing = Strategy("DE", 1_900, inclusive: false, new EuDistanceSalesTurnover(0, 990_001))
            .TaxFor(Input("EUR", "FR", 10_000));
        Assert.Equal((TaxScheme.Oss, 2_000L), (crossing.Scheme, crossing.TaxMinor));

        // The sale's value is NET of VAT: on an inclusive store 11900 gross is 10000 net, so 990000 + 10000 stays home.
        var inclusiveAt = Strategy("DE", 1_900, inclusive: true, new EuDistanceSalesTurnover(0, 990_000))
            .TaxFor(Input("EUR", "FR", 11_900));
        Assert.Equal((TaxScheme.Domestic, 1_900L), (inclusiveAt.Scheme, inclusiveAt.TaxMinor));
    }

    [Fact]
    public void A_preceding_year_over_the_threshold_puts_the_whole_current_year_on_OSS()
    {
        var previousOver = Strategy("DE", 1_900, inclusive: false, new EuDistanceSalesTurnover(1_000_001, 0))
            .TaxFor(Input("EUR", "IT", 100));
        Assert.Equal((TaxScheme.Oss, 22L), (previousOver.Scheme, previousOver.TaxMinor));

        // Exactly EUR 10,000 last year is NOT over it (Art. 59c: "has not exceeded").
        var previousAt = Strategy("DE", 1_900, inclusive: false, new EuDistanceSalesTurnover(1_000_000, 0))
            .TaxFor(Input("EUR", "IT", 100));
        Assert.Equal((TaxScheme.Domestic, 19L), (previousAt.Scheme, previousAt.TaxMinor));
    }

    [Fact]
    public void Domestic_and_non_EU_destinations_ignore_the_threshold_and_charge_the_home_rate()
    {
        foreach (var destination in new[] { "DE", "de", "US", "AU", "GB", "CH", "NO" })
        {
            var input = Input("EUR", destination, 10_000, shipping: 499);
            var eu = Strategy("DE", 1_900, inclusive: true, OverThreshold).TaxFor(input);
            Assert.Equal(TaxScheme.Domestic, eu.Scheme);
            AssertSameBreakdown(new StorefrontRateTaxStrategy(1_900, taxInclusive: true).TaxFor(input), eu);
        }
    }

    [Fact]
    public void A_non_EUR_store_cannot_measure_the_EUR_threshold_so_its_cross_border_EU_sales_take_the_destination_rate()
    {
        // A Swedish home state selling in SEK: no FX source (ADR-0063 D13), so the safe direction — destination VAT.
        var breakdown = Strategy("SE", 2_500, inclusive: true, EuDistanceSalesTurnover.None).TaxFor(Input("SEK", "DE", 11_900));
        Assert.Equal((TaxScheme.Oss, 1_900L), (breakdown.Scheme, breakdown.TaxMinor));

        // ... while its domestic sales stay at its own rate.
        var domestic = Strategy("SE", 2_500, inclusive: true, EuDistanceSalesTurnover.None).TaxFor(Input("SEK", "SE", 12_500));
        Assert.Equal((TaxScheme.Domestic, 2_500L), (domestic.Scheme, domestic.TaxMinor));
    }

    [Fact]
    public void An_explicit_destination_overrides_the_ship_to_country()
    {
        // P3's bill-to (digital services are supplied where the customer belongs) arrives through the input.
        var input = new TaxCalculationInput("EUR", "DE", [new TaxLineInput(10_000, 0)], 0, DestinationCountry: "AT");
        var breakdown = Strategy("DE", 1_900, inclusive: false, OverThreshold).TaxFor(input);
        Assert.Equal((TaxScheme.Oss, 2_000L), (breakdown.Scheme, breakdown.TaxMinor));

        // Negative control: without it the ship-to (DE, home) decides.
        var shipTo = Strategy("DE", 1_900, inclusive: false, OverThreshold).TaxFor(input with { DestinationCountry = null });
        Assert.Equal((TaxScheme.Domestic, 1_900L), (shipTo.Scheme, shipTo.TaxMinor));
    }

    // ---- 3. effective dates -----------------------------------------------------------------------------

    [Fact]
    public void A_rate_change_takes_effect_at_local_midnight_on_its_effective_date()
    {
        // Slovakia 20 % → 23 % on 1 Jan 2025 (CET, UTC+1): 22:59:59Z is still 31 Dec there, 23:00:00Z is 1 Jan.
        Assert.Equal(2_000, OssTaxAt("SK", new DateTimeOffset(2024, 12, 31, 22, 59, 59, TimeSpan.Zero)));
        Assert.Equal(2_300, OssTaxAt("SK", new DateTimeOffset(2024, 12, 31, 23, 0, 0, TimeSpan.Zero)));

        // Estonia 22 % → 24 % on 1 Jul 2025 (EEST, UTC+3): a UTC-date rule would still charge 22 % at 21:30Z.
        Assert.Equal(2_200, OssTaxAt("EE", new DateTimeOffset(2025, 6, 30, 20, 59, 59, TimeSpan.Zero)));
        Assert.Equal(2_400, OssTaxAt("EE", new DateTimeOffset(2025, 6, 30, 21, 30, 0, TimeSpan.Zero)));

        // Finland 24 % → 25.5 % (1 Sep 2024), Romania 19 % → 21 % (1 Aug 2025), Luxembourg 17 → 16 → 17 (2023 only).
        Assert.Equal(2_400, OssTaxAt("FI", new DateTimeOffset(2024, 8, 31, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(2_550, OssTaxAt("FI", new DateTimeOffset(2024, 9, 1, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(1_900, OssTaxAt("RO", new DateTimeOffset(2025, 7, 31, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(2_100, OssTaxAt("RO", new DateTimeOffset(2025, 8, 1, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(1_700, OssTaxAt("LU", new DateTimeOffset(2022, 12, 31, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(1_600, OssTaxAt("LU", new DateTimeOffset(2023, 6, 1, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(1_700, OssTaxAt("LU", new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void A_future_rate_row_changes_nothing_before_its_date_and_everything_after_it()
    {
        // A law change shipped ahead of time: FR 20 % → 21 % from 1 Jan 2027 (hypothetical row, test table only).
        var table = new EuVatRateTable(EuVatRateTable.Current.Rows.Append(new EuVatRate("FR", "", new DateOnly(2027, 1, 1), 2_100)));
        var before = new EuOssTaxStrategy("DE", 1_900, false, table, OverThreshold, new DateTimeOffset(2026, 12, 31, 22, 59, 0, TimeSpan.Zero))
            .TaxFor(Input("EUR", "FR", 10_000));
        var after = new EuOssTaxStrategy("DE", 1_900, false, table, OverThreshold, new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero))
            .TaxFor(Input("EUR", "FR", 10_000));
        Assert.Equal(2_000, before.TaxMinor);
        Assert.Equal(2_100, after.TaxMinor);
    }

    // ---- 4. inclusive storefronts: the shelf price is the charge ------------------------------------------

    [Fact]
    public void An_inclusive_store_extracts_the_destination_VAT_from_the_unchanged_shelf_price()
    {
        // 11900 shelf + 499 shipping, German inclusive store, OSS sale to Hungary (27 %): the shopper pays 12399
        // whatever the rate; the VAT contained in it is 12399 × 2700 / 12700 = 2636.03 → 2636.
        var breakdown = Strategy("DE", 1_900, inclusive: true, OverThreshold).TaxFor(Input("EUR", "HU", 11_900, shipping: 499));

        Assert.True(breakdown.TaxInclusive);
        Assert.Equal(TaxScheme.Oss, breakdown.Scheme);
        Assert.Equal(2_636, breakdown.TaxMinor);
        Assert.Equal(StorefrontRateTaxStrategy.InclusiveTaxOn(12_399, 2_700), breakdown.TaxMinor);

        // Net + tax of every component is exactly what the shopper paid for it: the gross does not move.
        Assert.Equal(11_900, breakdown.Lines[0].TaxableBaseMinor + breakdown.Lines[0].TaxMinor);
        Assert.Equal(499, breakdown.Shipping.TaxableBaseMinor + breakdown.Shipping.TaxMinor);
        Assert.Equal(12_399, CheckoutCharge(11_900, 499, breakdown));

        // The same cart under the home rate charges the same 12399 — only the VAT inside it differs.
        var home = Strategy("DE", 1_900, inclusive: true, EuDistanceSalesTurnover.None).TaxFor(Input("EUR", "HU", 11_900, shipping: 499));
        Assert.Equal(12_399, CheckoutCharge(11_900, 499, home));
        Assert.NotEqual(home.TaxMinor, breakdown.TaxMinor);
    }

    [Fact]
    public void An_exclusive_store_adds_the_destination_VAT_on_top()
    {
        var breakdown = Strategy("DE", 1_900, inclusive: false, OverThreshold).TaxFor(Input("EUR", "HU", 10_000, shipping: 500));
        Assert.Equal(2_835, breakdown.TaxMinor); // (10000 + 500) × 27 %
        Assert.Equal(13_335, CheckoutCharge(10_000, 500, breakdown));
    }

    [Fact]
    public void Exempt_lines_stay_exempt_under_OSS()
    {
        var input = new TaxCalculationInput("EUR", "FR",
            [new TaxLineInput(10_000, 0), new TaxLineInput(5_000, 0, TaxExemptionReason.DestinationShipRule)], 499);
        var breakdown = Strategy("DE", 1_900, inclusive: false, OverThreshold).TaxFor(input);

        Assert.Equal(TaxScheme.Oss, breakdown.Scheme);
        Assert.Equal((TaxCategory.Exempt, 0, 0L), (breakdown.Lines[1].Category, breakdown.Lines[1].RateBps, breakdown.Lines[1].TaxMinor));
        Assert.Equal(2_100, breakdown.TaxMinor); // (10000 + 499) × 20 % = 2099.8 → 2100
        TaxBreakdownTests.AssertWellFormed(breakdown, input.Lines, 499);
    }

    // ---- 5. categories ----------------------------------------------------------------------------------

    [Fact]
    public void A_category_with_its_own_rate_is_taxed_at_it_and_shipping_follows_the_highest_rated_goods()
    {
        // Test-only rows (no reduced rate is seeded in the real table): printed books at 5.5 % in FR, 7 % in DE.
        var table = new EuVatRateTable(EuVatRateTable.Current.Rows.Concat(
        [
            new EuVatRate("FR", "books", EuVatRateTable.OssStart, 550),
            new EuVatRate("DE", "BOOKS", EuVatRateTable.OssStart, 700),
        ]));
        var input = new TaxCalculationInput("EUR", "FR",
        [
            new TaxLineInput(2_000, 0, TaxCategoryCode: "BOOKS"),
            new TaxLineInput(10_000, 1_000),
            new TaxLineInput(3_000, 0, TaxExemptionReason.DestinationShipRule, "BOOKS"),
        ], 500);

        var oss = new EuOssTaxStrategy("DE", 1_900, false, table, OverThreshold, Now).TaxFor(input);
        Assert.Equal(TaxScheme.Oss, oss.Scheme);
        Assert.Equal((550, 110L), (oss.Lines[0].RateBps, oss.Lines[0].TaxMinor));          // 2000 × 5.5 %
        Assert.Equal((2_000, 1_800L), (oss.Lines[1].RateBps, oss.Lines[1].TaxMinor));      // 9000 × 20 %
        Assert.Equal((2_000, 100L), (oss.Shipping.RateBps, oss.Shipping.TaxMinor));         // 500 × 20 % (highest)
        Assert.Equal(TaxCategory.Exempt, oss.Lines[2].Category);
        Assert.Equal(2_010, oss.TaxMinor);
        TaxBreakdownTests.AssertWellFormed(oss, input.Lines, 500);
        Assert.Equal([(TaxCategory.Standard, 550), (TaxCategory.Standard, 2_000), (TaxCategory.Exempt, 0)],
            oss.RateSummary.Select(r => (r.Category, r.RateBps)));

        // At home (below the threshold) the category takes the HOME table rate; standard lines the storefront's rate.
        var home = new EuOssTaxStrategy("DE", 1_900, false, table, EuDistanceSalesTurnover.None, Now).TaxFor(input);
        Assert.Equal(TaxScheme.Domestic, home.Scheme);
        Assert.Equal((700, 140L), (home.Lines[0].RateBps, home.Lines[0].TaxMinor));
        Assert.Equal((1_900, 1_710L), (home.Lines[1].RateBps, home.Lines[1].TaxMinor));
        Assert.Equal((1_900, 95L), (home.Shipping.RateBps, home.Shipping.TaxMinor));

        // Negative control: a category the destination has no row for is taxed at its standard rate.
        var unknown = new EuOssTaxStrategy("DE", 1_900, false, table, OverThreshold, Now)
            .TaxFor(Input("EUR", "IT", 10_000) with { Lines = [new TaxLineInput(10_000, 0, TaxCategoryCode: "BOOKS")] });
        Assert.Equal((2_200, 2_200L), (unknown.Lines[0].RateBps, unknown.TaxMinor));
    }

    [Fact]
    public void Mixed_rate_random_carts_stay_well_formed_in_both_regimes()
    {
        var table = new EuVatRateTable(EuVatRateTable.Current.Rows.Concat(
            [new EuVatRate("FR", "BOOKS", EuVatRateTable.OssStart, 550), new EuVatRate("FR", "FOOD", EuVatRateTable.OssStart, 0)]));
        var random = new Random(20_261_028);
        string?[] codes = [null, "BOOKS", "FOOD"];
        for (var run = 0; run < 5_000; run++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 6)).Select(_ =>
            {
                var amount = random.NextInt64(0, 200_000);
                return new TaxLineInput(amount, random.Next(0, 3) == 0 ? random.NextInt64(0, amount + 1) : 0,
                    random.Next(0, 5) == 0 ? TaxExemptionReason.DestinationShipRule : TaxExemptionReason.None,
                    codes[random.Next(codes.Length)]);
            }).ToList();
            var shipping = random.Next(0, 3) == 0 ? 0 : random.NextInt64(0, 5_000);
            var inclusive = random.Next(0, 2) == 0;
            var input = new TaxCalculationInput("EUR", "FR", lines, shipping);

            var breakdown = new EuOssTaxStrategy("DE", 1_900, inclusive, table, OverThreshold, Now).TaxFor(input);
            TaxBreakdownTests.AssertWellFormed(breakdown, lines, shipping);
            Assert.Equal(TaxScheme.Oss, breakdown.Scheme);
        }
    }

    // ---- 6. selection + byte-identical non-EU storefronts ---------------------------------------------------

    [Theory]
    [InlineData("DE", "DE")]
    [InlineData("de-by", "DE")]
    [InlineData("GR", "GR")]
    [InlineData("EL", "GR")]
    [InlineData("", null)]
    [InlineData("AU", null)]
    [InlineData("US-CA", null)]
    [InlineData("GB", null)]
    [InlineData("CH", null)]
    [InlineData("NO", null)]
    public void The_strategy_is_selected_by_the_storefronts_tax_jurisdiction(string jurisdiction, string? home)
    {
        var copy = Copy(jurisdiction, 1_900, inclusive: true);
        var strategy = TaxStrategySelector.For(copy, OverThreshold, Now);
        if (home is null)
        {
            Assert.IsType<StorefrontRateTaxStrategy>(strategy);
        }
        else
        {
            Assert.Equal(home, Assert.IsType<EuOssTaxStrategy>(strategy).HomeMemberState);
        }

        Assert.IsType<StorefrontRateTaxStrategy>(TaxStrategySelector.For(null, OverThreshold, Now));
    }

    [Fact]
    public void Only_an_EU_store_selling_into_another_member_state_needs_the_running_total()
    {
        Assert.True(TaxStrategySelector.NeedsDistanceSalesTurnover(Copy("DE", 1_900, true), "FR"));
        Assert.False(TaxStrategySelector.NeedsDistanceSalesTurnover(Copy("DE", 1_900, true), "DE"));
        Assert.False(TaxStrategySelector.NeedsDistanceSalesTurnover(Copy("DE", 1_900, true), "US"));
        Assert.False(TaxStrategySelector.NeedsDistanceSalesTurnover(Copy("AU", 1_000, true), "FR"));
        Assert.False(TaxStrategySelector.NeedsDistanceSalesTurnover(Copy("", 1_000, true), "FR"));
        Assert.False(TaxStrategySelector.NeedsDistanceSalesTurnover(null, "FR"));
    }

    [Fact]
    public void Non_EU_and_unconfigured_storefronts_are_byte_identical_to_the_storefront_rate_strategy()
    {
        // P1's property approach: seeded random carts, every non-EU jurisdiction (and none), any turnover, any
        // destination (EU ones included) — the selected strategy's breakdown equals StorefrontRateTaxStrategy's
        // field for field. EU storefronts selling domestically or below the threshold are identical too.
        string[] nonEu = ["", "AU", "US-CA", "NZ", "GB", "CH", "NO", "JP"];
        string[] destinations = ["AU", "US", "DE", "FR", "GB", "IT", "JP"];
        var random = new Random(20_261_028);
        for (var run = 0; run < 10_000; run++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 7)).Select(_ =>
            {
                var amount = random.Next(0, 4) == 0 ? random.Next(0, 3) : random.NextInt64(1, 500_000) * random.Next(1, 4);
                return new TaxLineInput(amount, random.Next(0, 3) == 0 ? random.NextInt64(0, amount + 1) : 0,
                    random.Next(0, 4) == 0 ? TaxExemptionReason.DestinationShipRule : TaxExemptionReason.None);
            }).ToList();
            var shipping = random.Next(0, 3) == 0 ? 0 : random.NextInt64(0, 5_000);
            var bps = random.Next(0, 5) == 0 ? 0 : random.Next(1, 10_001);
            var inclusive = random.Next(0, 2) == 0;
            var destination = destinations[random.Next(destinations.Length)];
            var currency = random.Next(0, 2) == 0 ? "EUR" : "AUD";
            var turnover = new EuDistanceSalesTurnover(random.NextInt64(0, 3_000_000), random.NextInt64(0, 3_000_000));
            var input = new TaxCalculationInput(currency, destination, lines, shipping);
            var expected = new StorefrontRateTaxStrategy(bps, inclusive).TaxFor(input);

            var copy = Copy(nonEu[random.Next(nonEu.Length)], bps, inclusive);
            AssertSameBreakdown(expected, TaxStrategySelector.For(copy, turnover, Now).TaxFor(input));

            // An EU (German) store: domestic and below-threshold sales are the same breakdown too.
            var german = TaxStrategySelector.For(Copy("DE", bps, inclusive), EuDistanceSalesTurnover.None, Now).TaxFor(
                input with { Currency = "EUR", ShipCountry = destination == "FR" || destination == "IT" ? "DE" : destination });
            AssertSameBreakdown(new StorefrontRateTaxStrategy(bps, inclusive).TaxFor(
                input with { Currency = "EUR", ShipCountry = destination == "FR" || destination == "IT" ? "DE" : destination }), german);
        }
    }

    // ---- 7. the running total's rows --------------------------------------------------------------------

    [Fact]
    public void A_confirmed_cross_border_EU_order_contributes_its_net_value_and_nothing_else_does()
    {
        var crossBorder = Order("DE", "FR", "EUR", TaxScheme.Domestic);
        var sale = EuDistanceSale.For(crossBorder, new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero), Now);
        Assert.NotNull(sale);
        Assert.Equal((crossBorder.Id, crossBorder.TenantId, 2026, "DE", "FR", "EUR"),
            (sale.OrderId, sale.TenantId, sale.Year, sale.HomeCountry, sale.DestinationCountry, sale.Currency));
        Assert.Equal(10_000 + 2_521 + 420, sale.NetMinor); // Σ line bases (net of VAT) + shipping base

        Assert.Equal("DE", EuDistanceSale.For(Order("de-BY", "at", "eur", TaxScheme.Oss), Now, Now)?.HomeCountry);

        // Negative controls: domestic, non-EU store, non-EU destination, pre-P1b order (no jurisdiction recorded).
        Assert.Null(EuDistanceSale.For(Order("DE", "DE", "EUR", TaxScheme.Domestic), Now, Now));
        Assert.Null(EuDistanceSale.For(Order("AU", "FR", "EUR", TaxScheme.Domestic), Now, Now));
        Assert.Null(EuDistanceSale.For(Order("DE", "US", "EUR", TaxScheme.Domestic), Now, Now));
        Assert.Null(EuDistanceSale.For(Order("", "", "EUR", TaxScheme.NotRecorded), Now, Now));
    }

    [Fact]
    public void An_attempt_hands_its_jurisdiction_and_destination_to_the_order()
    {
        var attempt = new CheckoutAttempt
        {
            Id = Guid.CreateVersion7(),
            Email = "a@example.com",
            Currency = "EUR",
            PaymentIntentId = "pi_1",
            ShipName = "N",
            ShipLine1 = "L",
            ShipCity = "C",
            ShipPostcode = "P",
            ShipCountry = "FR",
            TaxScheme = TaxScheme.Oss,
            TaxJurisdiction = "DE-BY",
            TaxDestinationCountry = "FR",
        };

        var order = attempt.ToOrder(1_000, Now);
        Assert.Equal(("DE-BY", "FR", TaxScheme.Oss), (order.TaxJurisdiction, order.TaxDestinationCountry, order.TaxScheme));
    }

    [Fact]
    public void The_rate_table_refuses_rows_it_cannot_answer_for()
    {
        Assert.Throws<ArgumentException>(() => new EuVatRateTable([new EuVatRate("GB", "", EuVatRateTable.OssStart, 2_000)]));
        Assert.Throws<ArgumentException>(() => new EuVatRateTable([new EuVatRate("DE", "", EuVatRateTable.OssStart, 10_001)]));
        Assert.Throws<ArgumentException>(() => new EuVatRateTable(
            [new EuVatRate("DE", "", EuVatRateTable.OssStart, 1_900), new EuVatRate("de", "", EuVatRateTable.OssStart, 1_600)]));
        Assert.Throws<ArgumentException>(() => new EuOssTaxStrategy("CH", 810, true, EuVatRateTable.Current, EuDistanceSalesTurnover.None, Now));
        Assert.Null(EuVatRateTable.Current.StandardRateFor("US", DateOnly.FromDateTime(Now.UtcDateTime)));
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static EuOssTaxStrategy Strategy(string home, int homeBps, bool inclusive, EuDistanceSalesTurnover turnover) =>
        new(home, homeBps, inclusive, EuVatRateTable.Current, turnover, Now);

    private static TaxCalculationInput Input(string currency, string destination, long amount, long shipping = 0) =>
        new(currency, destination, [new TaxLineInput(amount, 0)], shipping);

    private static long OssTaxAt(string destination, DateTimeOffset at) =>
        new EuOssTaxStrategy("DE", 1_900, false, EuVatRateTable.Current, OverThreshold, at).TaxFor(Input("EUR", destination, 10_000)).TaxMinor;

    /// <summary>What checkout charges (CheckoutEndpoints): inclusive = the listed amount, exclusive = it + tax.</summary>
    private static long CheckoutCharge(long goods, long shipping, TaxBreakdown breakdown) =>
        breakdown.TaxInclusive ? goods + shipping : goods + shipping + breakdown.TaxMinor;

    private static StorefrontTaxCopy Copy(string jurisdiction, int bps, bool inclusive) => new()
    {
        StorefrontId = Guid.NewGuid(),
        Currency = "EUR",
        TaxRateBasisPoints = bps,
        TaxInclusive = inclusive,
        IsLive = true,
        TaxJurisdiction = jurisdiction,
    };

    private static Order Order(string jurisdiction, string destination, string currency, TaxScheme scheme) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        Email = "a@example.com",
        Currency = currency,
        PaymentIntentId = "pi_1",
        ShipName = "N",
        ShipLine1 = "L",
        ShipCity = "C",
        ShipPostcode = "P",
        ShipCountry = destination,
        TaxScheme = scheme,
        TaxJurisdiction = jurisdiction,
        TaxDestinationCountry = destination,
        ShippingTaxableBaseMinor = 420,
        Lines =
        [
            new OrderLine { Title = "A", Quantity = 1, UnitPriceMinor = 11_900, TaxableBaseMinor = 10_000, TaxMinor = 1_900 },
            new OrderLine { Title = "B", Quantity = 1, UnitPriceMinor = 2_521, TaxableBaseMinor = 2_521, TaxCategory = TaxCategory.Exempt },
        ],
    };

    private static void AssertSameBreakdown(TaxBreakdown expected, TaxBreakdown actual)
    {
        Assert.Equal(expected.Lines, actual.Lines);
        Assert.Equal(expected.Shipping, actual.Shipping);
        Assert.Equal(expected.Scheme, actual.Scheme);
        Assert.Equal(expected.TaxInclusive, actual.TaxInclusive);
        Assert.Equal(expected.TaxMinor, actual.TaxMinor);
    }
}
