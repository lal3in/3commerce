using System.Text.Json;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// ADR-0063 § 16 (P1b follow-up), per-line destination: in a cart that mixes physical goods and electronically
/// supplied services, the goods (and the shipping) are taxed at the ship-to country and the services at the bill-to
/// country. Pinned here:
/// <list type="number">
/// <item>the place of supply per fulfilment type (<see cref="PlaceOfSupply"/>);</item>
/// <item>a mixed sale over the threshold taxes each destination at its own rate, one rounding per destination and
/// rate, shipping with the goods;</item>
/// <item>the threshold stays per sale and counts only the components supplied into another Member State;</item>
/// <item>single-destination and non-EU carts are byte-identical to the strategy before the split (an oracle copy of
/// it, P1's property-test approach);</item>
/// <item>the running total records one row per destination; the facts reach the order line and OrderConfirmed.</item>
/// </list>
/// </summary>
public class PerLineTaxDestinationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly EuDistanceSalesTurnover OverThreshold = new(EuOssTaxStrategy.ThresholdMinorEur + 1, 0);

    private static readonly FulfilmentType[] Digital = [FulfilmentType.DigitalDownload, FulfilmentType.Subscription, FulfilmentType.Usage];
    private static readonly FulfilmentType[] NotDigital =
        [FulfilmentType.Unassigned, FulfilmentType.Warehouse, FulfilmentType.Dropship, FulfilmentType.ManualService];

    // ---- 1. the place of supply ---------------------------------------------------------------------------

    [Fact]
    public void Electronically_supplied_lines_go_to_the_bill_to_country_and_every_other_line_to_the_ship_to()
    {
        foreach (var type in Digital)
        {
            Assert.True(PlaceOfSupply.IsElectronicallySupplied(type), type.ToString());
            Assert.Equal("FR", PlaceOfSupply.LineDestination(Cart("de", " fr "), Line(1_000, type)));
        }

        foreach (var type in NotDigital)
        {
            Assert.False(PlaceOfSupply.IsElectronicallySupplied(type), type.ToString());
            Assert.Equal("DE", PlaceOfSupply.LineDestination(Cart("de", "FR"), Line(1_000, type)));
        }

        // Negative controls: no bill-to → the cart's destination; an explicit cart destination wins over the ship-to.
        Assert.Equal("DE", PlaceOfSupply.LineDestination(Cart("DE", null), Line(1_000, FulfilmentType.DigitalDownload)));
        Assert.Equal("DE", PlaceOfSupply.LineDestination(Cart("DE", "  "), Line(1_000, FulfilmentType.DigitalDownload)));
        Assert.Equal("AT", PlaceOfSupply.LineDestination(Cart("DE", "FR") with { DestinationCountry = "AT" }, Line(1_000)));
    }

    [Fact]
    public void The_sale_destination_is_the_shared_one_or_for_a_split_sale_the_carts()
    {
        Assert.Equal("FR", PlaceOfSupply.SaleDestination(Cart("DE", "FR", Line(1_000, FulfilmentType.Usage))));
        Assert.Equal("DE", PlaceOfSupply.SaleDestination(Cart("DE", "FR", Line(1_000), Line(1_000, FulfilmentType.Usage))));
        Assert.Equal("DE", PlaceOfSupply.SaleDestination(Cart("DE", "DE", Line(1_000), Line(1_000, FulfilmentType.Usage))));
    }

    // ---- 2. a mixed sale over the threshold ---------------------------------------------------------------

    [Fact]
    public void Over_the_threshold_goods_take_the_ship_to_rate_and_services_the_bill_to_rate_with_shipping_on_the_goods()
    {
        // German inclusive store, OSS. Goods 12000 shipped to Austria (20 %) + 600 shipping; an e-book 12700 billed
        // to Hungary (27 %). Austria: (12000 + 600) × 2000/12000 = 2100. Hungary: 12700 × 2700/12700 = 2700.
        var input = Cart("AT", "HU", Line(12_000), Line(12_700, FulfilmentType.DigitalDownload)) with { ShippingMinor = 600 };
        var breakdown = Strategy(inclusive: true, OverThreshold).TaxFor(input);

        Assert.Equal(TaxScheme.Oss, breakdown.Scheme);
        Assert.Equal(["AT", "HU"], breakdown.LineDestinations!);
        Assert.Equal("AT", breakdown.SaleDestination);
        Assert.Equal(new LineTax(TaxCategory.Standard, 2_000, 10_000, 2_000), breakdown.Lines[0]);
        Assert.Equal(new LineTax(TaxCategory.Standard, 2_700, 10_000, 2_700), breakdown.Lines[1]);
        Assert.Equal(new LineTax(TaxCategory.Standard, 2_000, 500, 100), breakdown.Shipping);
        Assert.Equal(4_800, breakdown.TaxMinor);
    }

    [Fact]
    public void Shipping_follows_the_goods_home_even_when_a_service_abroad_has_the_higher_rate()
    {
        // Goods stay in Germany (19 %); a French subscription (20 %) is the higher rate. Before the split the
        // shipping rode the highest-rated group — here it must ride the German goods.
        var input = Cart("DE", "FR", Line(10_000), Line(10_000, FulfilmentType.Subscription)) with { ShippingMinor = 1_000 };
        var breakdown = Strategy(inclusive: false, OverThreshold).TaxFor(input);

        Assert.Equal(TaxScheme.Oss, breakdown.Scheme);
        Assert.Equal((1_900, 2_000), (breakdown.Lines[0].RateBps, breakdown.Lines[1].RateBps));
        Assert.Equal(new LineTax(TaxCategory.Standard, 1_900, 1_000, 190), breakdown.Shipping);
        Assert.Equal("DE", breakdown.SaleDestination);
    }

    [Fact]
    public void Two_destinations_at_the_same_rate_are_rounded_separately()
    {
        // France and Austria are both 20 %. Exclusive 20 % of 1003 = 200.6 → 201 each (402); one rounding of the
        // combined 2006 would give 401.2 → 401. Each country's VAT is its own.
        var input = Cart("AT", "FR", Line(1_003), Line(1_003, FulfilmentType.DigitalDownload));
        var breakdown = Strategy(inclusive: false, OverThreshold).TaxFor(input);
        Assert.Equal((201, 201, 402L), (breakdown.Lines[0].TaxMinor, breakdown.Lines[1].TaxMinor, breakdown.TaxMinor));
    }

    [Fact]
    public void When_the_goods_are_exempt_the_shipping_follows_them_even_if_a_service_is_taxed()
    {
        var input = Cart("AT", "FR", Line(5_000, exempt: true), Line(10_000, FulfilmentType.DigitalDownload)) with { ShippingMinor = 700 };
        var breakdown = Strategy(inclusive: false, OverThreshold).TaxFor(input);

        Assert.Equal(TaxCategory.Exempt, breakdown.Lines[0].Category);
        Assert.Equal(new LineTax(TaxCategory.Standard, 2_000, 10_000, 2_000), breakdown.Lines[1]);
        Assert.Equal(new LineTax(TaxCategory.Exempt, 0, 700, 0, TaxExemptionReason.ShippingFollowsExemptGoods), breakdown.Shipping);
    }

    [Fact]
    public void A_digital_only_cart_is_taxed_wholly_at_the_bill_to_country()
    {
        var input = Cart("DE", "IT", Line(10_000, FulfilmentType.DigitalDownload), Line(5_000, FulfilmentType.Usage));
        var breakdown = Strategy(inclusive: false, OverThreshold).TaxFor(input);
        Assert.Equal(("IT", TaxScheme.Oss, 3_300L), (breakdown.SaleDestination, breakdown.Scheme, breakdown.TaxMinor));
        Assert.All(breakdown.Lines, l => Assert.Equal(2_200, l.RateBps));
    }

    // ---- 3. the threshold stays per sale -------------------------------------------------------------------

    [Fact]
    public void Only_the_components_supplied_into_another_member_state_count_toward_the_threshold()
    {
        // 9,900.00 already this year. Goods 500.00 stay in Germany; a 50.00 service goes to France: 9,950.00 ≤
        // 10,000.00 → home rate for everything. The German goods do not count.
        var underTurnover = new EuDistanceSalesTurnover(0, 990_000);
        var under = Strategy(inclusive: false, underTurnover).TaxFor(
            Cart("DE", "FR", Line(50_000), Line(5_000, FulfilmentType.DigitalDownload)));
        Assert.Equal(TaxScheme.Domestic, under.Scheme);
        Assert.All(under.Lines, l => Assert.Equal(1_900, l.RateBps));
        Assert.Equal(["DE", "FR"], under.LineDestinations!); // the destinations are recorded all the same

        // A 100.01 service crosses it: the crossing sale is OSS, but only its French part takes the French rate.
        var crossing = Strategy(inclusive: false, underTurnover).TaxFor(
            Cart("DE", "FR", Line(50_000), Line(10_001, FulfilmentType.DigitalDownload)));
        Assert.Equal(TaxScheme.Oss, crossing.Scheme);
        Assert.Equal((1_900, 2_000), (crossing.Lines[0].RateBps, crossing.Lines[1].RateBps));
    }

    [Fact]
    public void A_split_sale_needs_the_running_total_when_any_part_crosses_a_border()
    {
        var german = Copy("DE");
        Assert.True(TaxStrategySelector.NeedsDistanceSalesTurnover(german, Cart("DE", "FR", Line(1), Line(1, FulfilmentType.Usage))));
        Assert.True(TaxStrategySelector.NeedsDistanceSalesTurnover(german, Cart("FR", "DE", Line(1), Line(1, FulfilmentType.Usage))));
        Assert.False(TaxStrategySelector.NeedsDistanceSalesTurnover(german, Cart("DE", "FR", Line(1))));
        Assert.False(TaxStrategySelector.NeedsDistanceSalesTurnover(german, Cart("DE", "US", Line(1), Line(1, FulfilmentType.Usage))));
        Assert.False(TaxStrategySelector.NeedsDistanceSalesTurnover(Copy("AU"), Cart("DE", "FR", Line(1, FulfilmentType.Usage))));
    }

    // ---- 4. byte-identical where nothing is split ----------------------------------------------------------

    [Fact]
    public void Single_destination_carts_are_byte_identical_to_the_strategy_before_the_split()
    {
        // Seeded random carts whose lines all share one destination — no digital line, a bill-to equal to the
        // ship-to, no bill-to, or a digital-only cart billed elsewhere (then the oracle is asked for that country) —
        // over any turnover, currency, regime, category mix and exemption: field for field what the pre-split
        // strategy (an oracle copy below) answered.
        var table = new EuVatRateTable(EuVatRateTable.Current.Rows.Concat(
            [new EuVatRate("FR", "BOOKS", EuVatRateTable.OssStart, 550), new EuVatRate("DE", "BOOKS", EuVatRateTable.OssStart, 700)]));
        string[] countries = ["DE", "FR", "IT", "AT", "US", "GB", "CH"];
        string?[] codes = [null, null, "BOOKS"];
        var random = new Random(20_261_105);
        for (var run = 0; run < 10_000; run++)
        {
            var shipTo = countries[random.Next(countries.Length)];
            var shape = random.Next(4);
            var billTo = shape switch
            {
                0 => countries[random.Next(countries.Length)], // no digital line: the bill-to is irrelevant
                1 => shipTo,
                2 => null,
                _ => countries[random.Next(countries.Length)], // digital only
            };
            var lines = Enumerable.Range(0, random.Next(1, 6)).Select(_ =>
            {
                var amount = random.Next(0, 4) == 0 ? random.Next(0, 3) : random.NextInt64(1, 300_000);
                var type = shape switch
                {
                    0 => NotDigital[random.Next(NotDigital.Length)],
                    3 => Digital[random.Next(Digital.Length)],
                    _ => random.Next(2) == 0 ? Digital[random.Next(Digital.Length)] : NotDigital[random.Next(NotDigital.Length)],
                };
                return new TaxLineInput(amount, random.Next(0, 3) == 0 ? random.NextInt64(0, amount + 1) : 0,
                    random.Next(0, 5) == 0 ? TaxExemptionReason.DestinationShipRule : TaxExemptionReason.None,
                    codes[random.Next(codes.Length)], type);
            }).ToList();
            var input = new TaxCalculationInput(
                random.Next(0, 4) == 0 ? "SEK" : "EUR", shipTo, lines, random.Next(0, 3) == 0 ? 0 : random.NextInt64(0, 5_000),
                BillCountry: billTo);
            var inclusive = random.Next(0, 2) == 0;
            var homeBps = random.Next(0, 5) == 0 ? 0 : random.Next(1, 3_000);
            var turnover = new EuDistanceSalesTurnover(random.NextInt64(0, 2_000_000), random.NextInt64(0, 2_000_000));

            var destination = shape == 3 && billTo is not null ? billTo : shipTo;
            var expected = PreSplitOracle.TaxFor("DE", homeBps, inclusive, table, turnover, Now, input with { DestinationCountry = destination });
            var actual = new EuOssTaxStrategy("DE", homeBps, inclusive, table, turnover, Now).TaxFor(input);
            AssertSameBreakdown(expected, actual);
            Assert.Equal(destination, actual.SaleDestination);
            Assert.All(actual.LineDestinations!, d => Assert.Equal(destination, d));
        }
    }

    [Fact]
    public void Non_EU_storefronts_ignore_the_bill_to_and_report_no_line_destinations()
    {
        var random = new Random(11_05);
        foreach (var jurisdiction in new[] { "", "AU", "US-CA", "GB", "CH" })
        {
            for (var run = 0; run < 1_000; run++)
            {
                var lines = Enumerable.Range(0, random.Next(1, 5)).Select(_ => new TaxLineInput(random.NextInt64(1, 100_000), 0,
                    Fulfilment: random.Next(2) == 0 ? FulfilmentType.DigitalDownload : FulfilmentType.Warehouse)).ToList();
                var input = new TaxCalculationInput("EUR", "DE", lines, random.NextInt64(0, 1_000), BillCountry: "FR");
                var copy = Copy(jurisdiction, random.Next(0, 3_000), random.Next(2) == 0);
                var actual = TaxStrategySelector.For(copy, OverThreshold, Now).TaxFor(input);
                AssertSameBreakdown(new StorefrontRateTaxStrategy(copy.TaxRateBasisPoints, copy.TaxInclusive).TaxFor(input), actual);
                Assert.Null(actual.LineDestinations);
                Assert.Null(actual.SaleDestination);
            }
        }
    }

    [Fact]
    public void Split_random_carts_stay_well_formed_in_both_regimes()
    {
        string[] countries = ["DE", "FR", "IT", "AT", "HU", "US"];
        var random = new Random(5_11_2026);
        for (var run = 0; run < 5_000; run++)
        {
            var lines = Enumerable.Range(0, random.Next(2, 7)).Select(_ =>
            {
                var amount = random.NextInt64(0, 200_000);
                return new TaxLineInput(amount, random.Next(0, 3) == 0 ? random.NextInt64(0, amount + 1) : 0,
                    random.Next(0, 5) == 0 ? TaxExemptionReason.DestinationShipRule : TaxExemptionReason.None,
                    Fulfilment: random.Next(2) == 0 ? FulfilmentType.Usage : FulfilmentType.Dropship);
            }).ToList();
            var shipping = random.Next(0, 3) == 0 ? 0 : random.NextInt64(0, 5_000);
            var input = new TaxCalculationInput(
                "EUR", countries[random.Next(countries.Length)], lines, shipping, BillCountry: countries[random.Next(countries.Length)]);
            var breakdown = Strategy(random.Next(2) == 0, OverThreshold).TaxFor(input);

            Assert.Equal(lines.Count, breakdown.Lines.Count);
            Assert.Equal(breakdown.TaxMinor, breakdown.Lines.Sum(l => l.TaxMinor) + breakdown.Shipping.TaxMinor);
            Assert.Equal(breakdown.TaxMinor, breakdown.RateSummary.Sum(r => r.TaxMinor));
            Assert.All(breakdown.Lines, l => Assert.True(l.TaxMinor >= 0 && l.TaxableBaseMinor >= 0, $"negative fact {l}"));
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Exemption != TaxExemptionReason.None)
                {
                    Assert.Equal((TaxCategory.Exempt, 0L), (breakdown.Lines[i].Category, breakdown.Lines[i].TaxMinor));
                }
                else
                {
                    // Each taxable line carries its destination's rate (home when it is not another Member State).
                    var destination = breakdown.LineDestinations![i];
                    var expected = destination is "DE" or "US" ? 1_900 : EuVatRateTable.Current.StandardRateFor(destination, DateOnly.FromDateTime(Now.UtcDateTime));
                    Assert.Equal(expected, breakdown.Lines[i].RateBps);
                }
            }

            // The shopper pays the listed amount (inclusive) or it plus the tax (exclusive): every taxable base is
            // exactly what was taxed, the shipping counted only when it was taxed.
            var taxable = breakdown.Lines.Append(breakdown.Shipping).Where(c => c.Category == TaxCategory.Standard)
                .Sum(c => breakdown.TaxInclusive ? c.TaxableBaseMinor + c.TaxMinor : c.TaxableBaseMinor);
            var goods = lines.Where(l => l.Exemption == TaxExemptionReason.None).Sum(l => l.AmountMinor - l.DiscountMinor);
            var taxedShipping = breakdown.Shipping.Category == TaxCategory.Standard ? shipping : 0;
            Assert.Equal(goods + taxedShipping, taxable);
        }
    }

    // ---- 5. the facts: running total, order line, OrderConfirmed --------------------------------------------

    [Fact]
    public void A_split_sale_records_one_running_total_row_per_destination_it_crossed_into()
    {
        var order = Order("DE", "AT",
            LineAt("FR", 1_000), LineAt("IT", 2_000), LineAt("FR", 300), LineAt("DE", 5_000), LineAt("", 4_000), LineAt("US", 900));
        var rows = EuDistanceSale.For(order, Now, Now);

        // AT: the shipping (the order's destination) + the line recorded before per-line destinations ("" → AT).
        Assert.Equal([("AT", 420L + 4_000), ("FR", 1_300L), ("IT", 2_000L)], rows.Select(r => (r.DestinationCountry, r.NetMinor)));
        Assert.All(rows, r => Assert.Equal((order.Id, "DE", "EUR"), (r.OrderId, r.HomeCountry, r.Currency)));

        // Negative control: everything at home → nothing counts.
        Assert.Empty(EuDistanceSale.For(Order("DE", "DE", LineAt("DE", 1_000), LineAt("US", 500)), Now, Now));

        // Goods home, a service abroad: only the service counts, the shipping (with the goods at home) does not.
        Assert.Equal([("FR", 1_000L)], EuDistanceSale.For(Order("DE", "DE", LineAt("DE", 9_000), LineAt("FR", 1_000)), Now, Now)
            .Select(r => (r.DestinationCountry, r.NetMinor)));
    }

    [Fact]
    public void The_line_destination_travels_from_the_attempt_to_the_order_and_onto_OrderConfirmed()
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
            ShipCountry = "DE",
            TaxScheme = TaxScheme.Oss,
            TaxJurisdiction = "DE",
            TaxDestinationCountry = "DE",
            Lines =
            [
                new CheckoutAttemptLine { Title = "Book", Quantity = 1, UnitPriceMinor = 1_000, TaxDestinationCountry = "DE" },
                new CheckoutAttemptLine { Title = "E-book", Quantity = 1, UnitPriceMinor = 500, TaxDestinationCountry = "FR" },
                new CheckoutAttemptLine { Title = "Legacy", Quantity = 1, UnitPriceMinor = 200 },
            ],
        };

        var order = attempt.ToOrder(1, Now);
        Assert.Equal(["DE", "FR", ""], order.Lines.Select(l => l.TaxDestinationCountry));
        Assert.Equal("DE", order.Lines[2].DestinationOr(order.TaxDestinationCountry));

        var confirmed = OrderConfirmedFactory.From(order);
        Assert.Equal(["DE", "FR", ""], confirmed.Lines.Select(l => l.TaxDestinationCountry));
        Assert.Equal("DE", confirmed.TaxDestinationCountry);
    }

    [Fact]
    public void An_OrderLineInfo_published_before_the_field_reads_an_empty_destination()
    {
        var legacy = """{"ProductId":"00000000-0000-0000-0000-000000000001","VariantId":null,"SupplierId":null,"Title":"T","Quantity":1,"FulfilmentType":2,"BillingMode":1,"UnitPriceMinor":100}""";
        var line = JsonSerializer.Deserialize<OrderLineInfo>(legacy)!;
        Assert.Equal(string.Empty, line.TaxDestinationCountry);

        var roundTrip = JsonSerializer.Deserialize<OrderLineInfo>(JsonSerializer.Serialize(line with { TaxDestinationCountry = "FR" }))!;
        Assert.Equal("FR", roundTrip.TaxDestinationCountry);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static EuOssTaxStrategy Strategy(bool inclusive, EuDistanceSalesTurnover turnover) =>
        new("DE", 1_900, inclusive, EuVatRateTable.Current, turnover, Now);

    private static TaxLineInput Line(long amount, FulfilmentType type = FulfilmentType.Warehouse, bool exempt = false) =>
        new(amount, 0, exempt ? TaxExemptionReason.DestinationShipRule : TaxExemptionReason.None, Fulfilment: type);

    private static TaxCalculationInput Cart(string shipTo, string? billTo, params TaxLineInput[] lines) =>
        new("EUR", shipTo, lines, BillCountry: billTo);

    private static StorefrontTaxCopy Copy(string jurisdiction, int bps = 1_900, bool inclusive = true) => new()
    {
        StorefrontId = Guid.NewGuid(),
        Currency = "EUR",
        TaxRateBasisPoints = bps,
        TaxInclusive = inclusive,
        IsLive = true,
        TaxJurisdiction = jurisdiction,
    };

    private static OrderLine LineAt(string destination, long netMinor) =>
        new() { Title = "L", Quantity = 1, UnitPriceMinor = netMinor, TaxableBaseMinor = netMinor, TaxDestinationCountry = destination };

    private static Order Order(string jurisdiction, string destination, params OrderLine[] lines) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        Email = "a@example.com",
        Currency = "EUR",
        PaymentIntentId = "pi_1",
        ShipName = "N",
        ShipLine1 = "L",
        ShipCity = "C",
        ShipPostcode = "P",
        ShipCountry = destination,
        TaxScheme = TaxScheme.Oss,
        TaxJurisdiction = jurisdiction,
        TaxDestinationCountry = destination,
        ShippingTaxableBaseMinor = 420,
        Lines = [.. lines],
    };

    private static void AssertSameBreakdown(TaxBreakdown expected, TaxBreakdown actual)
    {
        Assert.Equal(expected.Lines, actual.Lines);
        Assert.Equal(expected.Shipping, actual.Shipping);
        Assert.Equal(expected.Scheme, actual.Scheme);
        Assert.Equal(expected.TaxInclusive, actual.TaxInclusive);
        Assert.Equal(expected.TaxMinor, actual.TaxMinor);
    }

    /// <summary>
    /// A VERBATIM copy of <see cref="EuOssTaxStrategy"/>'s computation before the per-line destination (develop @
    /// 4b44ed5b): the whole cart at one destination, shipping on the highest-rated group — the reference every
    /// single-destination cart must still reproduce to the minor unit.
    /// </summary>
    private static class PreSplitOracle
    {
        public static TaxBreakdown TaxFor(
            string home, int homeRateBps, bool taxInclusive, EuVatRateTable rates, EuDistanceSalesTurnover turnover,
            DateTimeOffset supplyTime, TaxCalculationInput input)
        {
            var homeDate = EuMemberStates.LocalDate(home, supplyTime);
            var domestic = TaxAtRates(
                input, line => rates.CategoryRateFor(home, line.TaxCategoryCode, homeDate) ?? homeRateBps, homeRateBps,
                taxInclusive, TaxScheme.Domestic);

            var destination = EuMemberStates.Normalize(input.DestinationCountry ?? input.ShipCountry);
            if (destination is null || destination == home || !OssApplies(input.Currency, domestic, turnover))
            {
                return domestic;
            }

            var destinationDate = EuMemberStates.LocalDate(destination, supplyTime);
            var standard = rates.StandardRateFor(destination, destinationDate)!.Value;
            return TaxAtRates(
                input, line => rates.CategoryRateFor(destination, line.TaxCategoryCode, destinationDate) ?? standard, standard,
                taxInclusive, TaxScheme.Oss);
        }

        private static bool OssApplies(string currency, TaxBreakdown domestic, EuDistanceSalesTurnover turnover)
        {
            if (!string.Equals(currency, "EUR", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return turnover.PreviousYearMinorEur > EuOssTaxStrategy.ThresholdMinorEur
                || turnover.CurrentYearMinorEur + EuOssTaxStrategy.DistanceSaleValue(domestic) > EuOssTaxStrategy.ThresholdMinorEur;
        }

        private static TaxBreakdown TaxAtRates(
            TaxCalculationInput input, Func<TaxLineInput, int> rateOf, int defaultRateBps, bool taxInclusive, TaxScheme scheme)
        {
            var lines = input.Lines;
            var rates = new int[lines.Count];
            var distinct = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Exemption == TaxExemptionReason.None)
                {
                    rates[i] = rateOf(lines[i]);
                    distinct.Add(rates[i]);
                }
            }

            if (distinct.Count <= 1)
            {
                var rate = distinct.Count == 1 ? distinct.First() : defaultRateBps;
                return new StorefrontRateTaxStrategy(rate, taxInclusive).TaxFor(input) with { Scheme = scheme };
            }

            var shippingRate = distinct.FirstOrDefault(r => Enumerable.Range(0, lines.Count)
                .Any(i => lines[i].Exemption == TaxExemptionReason.None && rates[i] == r && lines[i].AmountMinor > 0), distinct.First());
            var facts = new LineTax[lines.Count];
            LineTax? shipping = null;
            foreach (var rate in distinct)
            {
                var masked = lines
                    .Select((l, i) => l.Exemption == TaxExemptionReason.None && rates[i] != rate
                        ? l with { Exemption = TaxExemptionReason.DestinationShipRule }
                        : l)
                    .ToList();
                var group = new StorefrontRateTaxStrategy(rate, taxInclusive).TaxFor(
                    input with { Lines = masked, ShippingMinor = rate == shippingRate ? input.ShippingMinor : 0 });
                for (var i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Exemption != TaxExemptionReason.None || rates[i] == rate)
                    {
                        facts[i] = group.Lines[i];
                    }
                }

                if (rate == shippingRate)
                {
                    shipping = group.Shipping;
                }
            }

            return new TaxBreakdown(facts, shipping!, scheme, taxInclusive);
        }
    }
}
