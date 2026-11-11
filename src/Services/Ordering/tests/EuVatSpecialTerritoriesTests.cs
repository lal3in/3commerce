using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// ADR-0063 § 16 (P1b follow-up), special territories: the territorial rules of the EU VAT area a country code alone
/// cannot answer (<see cref="EuVatTerritories"/>). Pinned here:
/// <list type="number">
/// <item>every Art. 6 territory is found by each of its identifiers — an ISO 3166-1 code, an ISO 3166-2 subdivision,
/// a verified postcode prefix — and a mainland address of the same country (or the same prefix in another country) is
/// not (negative controls);</item>
/// <item>an unverified postcode is recognised but NOT applied (taxed as the Member State: never under-collected);</item>
/// <item>Northern Ireland is <c>XI</c> for goods (UK 20 %, OSS, counts toward the threshold) and the UK for services;
/// Monaco is France (Art. 7);</item>
/// <item>a supply into a territory outside the EU VAT area is an export: zero-rated with
/// <see cref="TaxExemptionReason.OutsideEuVatArea"/>, scheme Export when the whole sale goes there, at any turnover, and
/// never counted toward the threshold.</item>
/// </list>
/// </summary>
public class EuVatSpecialTerritoriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly EuDistanceSalesTurnover OverThreshold = new(EuOssTaxStrategy.ThresholdMinorEur + 1, 0);

    // ---- 1. detection ---------------------------------------------------------------------------------------

    [Theory]
    // Canary Islands — Art. 6(1)(b)
    [InlineData("ES", "ES-CN", null, "ES-CANARIAS", "region ES-CN")]
    [InlineData("ES", "cn", null, "ES-CANARIAS", "region ES-CN")]
    [InlineData("ES", "ES-GC", null, "ES-CANARIAS", "region ES-GC")]
    [InlineData("ES", "ES-TF", null, "ES-CANARIAS", "region ES-TF")]
    [InlineData("ES", null, "35001", "ES-CANARIAS", "postcode 35")]
    [InlineData("ES", null, " 38 001 ", "ES-CANARIAS", "postcode 38")]
    [InlineData("IC", null, null, "ES-CANARIAS", "country IC")]
    // Ceuta, Melilla — Art. 6(2)(c)/(d)
    [InlineData("ES", "ES-CE", null, "ES-CEUTA", "region ES-CE")]
    [InlineData("ES", null, "51001", "ES-CEUTA", "postcode 51")]
    [InlineData("EA", null, null, "ES-CEUTA", "country EA")]
    [InlineData("ES", "ES-ML", null, "ES-MELILLA", "region ES-ML")]
    [InlineData("ES", null, "52001", "ES-MELILLA", "postcode 52")]
    // Åland — Art. 6(1)(d)
    [InlineData("FI", "FI-01", null, "FI-ALAND", "region FI-01")]
    [InlineData("FI", null, "22100", "FI-ALAND", "postcode 22")]
    [InlineData("FI", null, "AX-22100", "FI-ALAND", "postcode 22")]
    [InlineData("FI", null, "FI-22100", "FI-ALAND", "postcode 22")]
    [InlineData("AX", null, "22100", "FI-ALAND", "country AX")]
    // Mount Athos — Art. 6(1)(a), by its ISO region (its postcode is unverified, below)
    [InlineData("GR", "GR-69", null, "GR-ATHOS", "region GR-69")]
    // French outermost regions — Art. 6(1)(c)
    [InlineData("FR", null, "97110", "FR-GUADELOUPE", "postcode 971")]
    [InlineData("FR", "FR-971", null, "FR-GUADELOUPE", "region FR-971")]
    [InlineData("GP", null, null, "FR-GUADELOUPE", "country GP")]
    [InlineData("FR", null, "97200", "FR-MARTINIQUE", "postcode 972")]
    [InlineData("MQ", null, null, "FR-MARTINIQUE", "country MQ")]
    [InlineData("FR", null, "97300", "FR-GUYANE", "postcode 973")]
    [InlineData("GF", null, null, "FR-GUYANE", "country GF")]
    [InlineData("FR", null, "97400", "FR-REUNION", "postcode 974")]
    [InlineData("RE", null, null, "FR-REUNION", "country RE")]
    [InlineData("FR", null, "97600", "FR-MAYOTTE", "postcode 976")]
    [InlineData("YT", null, null, "FR-MAYOTTE", "country YT")]
    [InlineData("FR", "FR-MF", null, "FR-SAINT-MARTIN", "region FR-MF")]
    [InlineData("MF", null, null, "FR-SAINT-MARTIN", "country MF")]
    [InlineData("FR", null, "97150", "FR-GUADELOUPE", "postcode 971")] // Saint-Martin's postcode, outside either way
    public void Every_special_territory_is_found_by_each_of_its_identifiers_and_applied(
        string country, string? region, string? postcode, string code, string matchedBy)
    {
        var match = EuVatTerritories.Match(country, region, postcode);
        Assert.NotNull(match);
        Assert.Equal((code, matchedBy, true), (match.Territory.Code, match.MatchedBy, match.Applies));

        var place = EuVatTerritories.Place(country, region, postcode, goods: true);
        Assert.True(place.IsOutsideEuVatArea);
        Assert.Equal(match.Territory.MemberState, place.Destination);
        Assert.Equal(place, EuVatTerritories.Place(country, region, postcode, goods: false)); // goods and services alike
    }

    [Theory]
    [InlineData("ES", null, "28013")] // Madrid
    [InlineData("ES", "ES-MD", "08001")]
    [InlineData("DE", null, "35037")] // Marburg: 35 is a Canaries prefix only in Spain
    [InlineData("FI", null, "00100")] // Helsinki
    [InlineData("FI", "FI-18", null)] // Uusimaa
    [InlineData("FR", null, "75001")] // Paris
    [InlineData("FR", null, "98000")] // a Monaco postcode with a French country: France
    [InlineData("GR", "GR-I", "10431")] // Athens
    [InlineData("IT", null, "00184")] // Rome
    [InlineData("DE", null, "10115")] // Berlin
    [InlineData("PT", null, "9000-001")] // Madeira is inside the EU VAT area
    [InlineData("", null, "35001")]
    public void A_mainland_address_is_not_a_special_territory(string country, string? region, string? postcode)
    {
        Assert.Null(EuVatTerritories.Match(country, region, postcode));
        Assert.False(EuVatTerritories.Place(country, region, postcode, goods: true).IsOutsideEuVatArea);
    }

    [Theory]
    [InlineData("DE", "27498", "DE-HELGOLAND")]
    [InlineData("DE", "78266", "DE-BUSINGEN")]
    [InlineData("IT", "23041", "IT-LIVIGNO")]
    [InlineData("IT", "22061", "IT-CAMPIONE")]
    [InlineData("GR", "63086", "GR-ATHOS")]
    public void An_unverified_postcode_is_recognised_but_not_applied_until_verified(string country, string postcode, string code)
    {
        var match = EuVatTerritories.Match(country, null, postcode);
        Assert.NotNull(match);
        Assert.Equal((code, false), (match.Territory.Code, match.Applies));
        Assert.False(match.Territory.PostcodesVerified);
        Assert.Contains("TO VERIFY", match.Territory.Note, StringComparison.Ordinal);

        // Taxed as the Member State until verified: over-collecting, never under-collecting.
        Assert.Equal(new TaxPlace(country), EuVatTerritories.Place(country, null, postcode, goods: true));
    }

    [Fact]
    public void Every_territory_is_well_formed_and_names_its_legal_basis()
    {
        Assert.Equal(15, EuVatTerritories.All.Count);
        Assert.Equal(EuVatTerritories.All.Count, EuVatTerritories.All.Select(t => t.Code).Distinct().Count());
        Assert.All(EuVatTerritories.All, t =>
        {
            Assert.NotNull(EuMemberStates.Normalize(t.MemberState));
            Assert.StartsWith("Art. 6", t.LegalBasis, StringComparison.Ordinal);
            Assert.All(t.Subdivisions, s => Assert.StartsWith($"{t.MemberState}-", s, StringComparison.Ordinal));
            Assert.All(t.PostcodePrefixes, p => Assert.True(p.All(char.IsAsciiDigit), p));
            Assert.All(t.CountryCodes, c => Assert.Null(EuMemberStates.Normalize(c))); // an alias is never a Member State
            Assert.True(t.CountryCodes.Count + t.Subdivisions.Count + t.PostcodePrefixes.Count > 0, t.Code);
        });
    }

    [Fact]
    public void Northern_Ireland_is_XI_for_goods_and_the_UK_for_services()
    {
        foreach (var (country, region, postcode) in new (string, string?, string?)[]
                 { ("XI", null, null), ("GB", "GB-NIR", null), ("gb", "NIR", null), ("GB", null, "BT1 1AA"), ("GB", null, "bt48 7aa") })
        {
            Assert.True(EuVatTerritories.IsNorthernIreland(country, region, postcode), $"{country} {region} {postcode}");
            Assert.Equal(new TaxPlace("XI"), EuVatTerritories.Place(country, region, postcode, goods: true));
            Assert.Equal(new TaxPlace("GB"), EuVatTerritories.Place(country, region, postcode, goods: false));
        }

        // Negative controls: the rest of the UK, a BT-looking non-postcode, Ireland.
        foreach (var (country, region, postcode) in new (string, string?, string?)[]
                 { ("GB", null, "SW1A 1AA"), ("GB", "GB-ENG", "B1 1AA"), ("GB", null, "BTX"), ("IE", null, "BT1 1AA"), ("IE", null, "D02 X285") })
        {
            Assert.False(EuVatTerritories.IsNorthernIreland(country, region, postcode), $"{country} {region} {postcode}");
            Assert.Equal(new TaxPlace(country.ToUpperInvariant()), EuVatTerritories.Place(country, region, postcode, goods: true));
        }

        Assert.Equal("XI", EuVatArea.Normalize(" xi "));
        Assert.Null(EuMemberStates.Normalize("XI")); // never a home Member State
        Assert.Null(EuMemberStates.OfJurisdiction("XI"));
        Assert.True(EuVatArea.AllTimeZonesResolve());
        // UK local midnight: 23:30 UTC on 31 Mar 2026 is already 1 April in Belfast (BST).
        Assert.Equal(new DateOnly(2026, 4, 1), EuVatArea.LocalDate("XI", new DateTimeOffset(2026, 3, 31, 23, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Monaco_is_taxed_as_France()
    {
        Assert.Equal(new TaxPlace("FR"), EuVatTerritories.Place("MC", null, "98000", goods: true));
        var breakdown = Strategy("DE", inclusive: false, OverThreshold).TaxFor(Cart("MC", null, "98000", Goods(10_000)));
        Assert.Equal(("FR", 2_000, TaxScheme.Oss), (breakdown.SaleDestination, breakdown.Lines[0].RateBps, breakdown.Scheme));
    }

    // ---- 2. the strategy: an export from the EU VAT area is zero-rated ---------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Goods_to_the_Canaries_are_zero_rated_with_their_shipping_at_any_turnover(bool over)
    {
        var turnover = over ? OverThreshold : EuDistanceSalesTurnover.None;
        foreach (var inclusive in new[] { true, false })
        {
            var input = Cart("ES", "ES-CN", "35001", Goods(11_900), Goods(5_000, exempt: true)) with { ShippingMinor = 499 };
            var breakdown = Strategy("DE", inclusive, turnover).TaxFor(input);

            Assert.Equal((0L, TaxScheme.Export, "ES"), (breakdown.TaxMinor, breakdown.Scheme, breakdown.SaleDestination));
            Assert.Equal(new LineTax(TaxCategory.Exempt, 0, 11_900, 0, TaxExemptionReason.OutsideEuVatArea), breakdown.Lines[0]);
            // The export reason takes precedence over the ship rule's.
            Assert.Equal(new LineTax(TaxCategory.Exempt, 0, 5_000, 0, TaxExemptionReason.OutsideEuVatArea), breakdown.Lines[1]);
            Assert.Equal(new LineTax(TaxCategory.Exempt, 0, 499, 0, TaxExemptionReason.OutsideEuVatArea), breakdown.Shipping);
            Assert.DoesNotContain(breakdown.RateSummary, r => r.TaxMinor != 0);
        }
    }

    [Fact]
    public void A_domestic_sale_into_a_territory_of_the_home_state_is_an_export_and_the_mainland_is_not()
    {
        // A Spanish store: mainland Spain is domestic (21 %), the Canaries, Ceuta and Melilla are exports.
        var mainland = Strategy("ES", inclusive: false, EuDistanceSalesTurnover.None).TaxFor(Cart("ES", null, "28013", Goods(10_000)));
        Assert.Equal((2_100L, TaxScheme.Domestic), (mainland.TaxMinor, mainland.Scheme));

        foreach (var postcode in new[] { "35001", "38001", "51001", "52001" })
        {
            var export = Strategy("ES", inclusive: false, EuDistanceSalesTurnover.None).TaxFor(Cart("ES", null, postcode, Goods(10_000)));
            Assert.Equal((0L, TaxScheme.Export), (export.TaxMinor, export.Scheme));
            Assert.Equal(TaxExemptionReason.OutsideEuVatArea, export.Lines[0].ExemptionReason);
        }

        // A French store into Réunion / Åland via its region.
        Assert.Equal(TaxScheme.Export, Strategy("FR", false, OverThreshold).TaxFor(Cart("FR", null, "97400", Goods(1_000))).Scheme);
        Assert.Equal(TaxScheme.Export, Strategy("FR", false, OverThreshold).TaxFor(Cart("FI", "FI-01", null, Goods(1_000))).Scheme);
    }

    [Fact]
    public void An_unverified_territory_is_still_taxed_as_its_member_state()
    {
        var heligoland = Strategy("DE", inclusive: false, OverThreshold).TaxFor(Cart("DE", null, "27498", Goods(10_000)));
        Assert.Equal((1_900L, TaxScheme.Domestic), (heligoland.TaxMinor, heligoland.Scheme));
        var livigno = Strategy("DE", inclusive: false, OverThreshold).TaxFor(Cart("IT", null, "23041", Goods(10_000)));
        Assert.Equal((2_200L, TaxScheme.Oss), (livigno.TaxMinor, livigno.Scheme));
    }

    [Fact]
    public void An_e_service_billed_to_a_territory_is_zero_rated_while_the_mainland_goods_are_taxed()
    {
        // Goods to Madrid (Spanish store, domestic 21 %), an e-book billed to Tenerife: exported. Shipping rides the goods.
        var input = new TaxCalculationInput("EUR", "ES", [Goods(10_000), Digital(5_000)], 1_000,
            BillCountry: "ES", ShipPostcode: "28013", BillRegion: "ES-TF");
        var breakdown = Strategy("ES", inclusive: false, EuDistanceSalesTurnover.None).TaxFor(input);

        Assert.Equal(TaxScheme.Domestic, breakdown.Scheme);
        Assert.Equal(new LineTax(TaxCategory.Standard, 2_100, 10_000, 2_100), breakdown.Lines[0]);
        Assert.Equal(TaxExemptionReason.OutsideEuVatArea, breakdown.Lines[1].ExemptionReason);
        Assert.Equal(new LineTax(TaxCategory.Standard, 2_100, 1_000, 210), breakdown.Shipping);
    }

    [Fact]
    public void Goods_exported_to_the_Canaries_with_an_e_book_billed_to_France_split_export_and_OSS()
    {
        var input = new TaxCalculationInput("EUR", "ES", [Goods(10_000), Digital(5_000)], 700,
            BillCountry: "FR", ShipPostcode: "35001");
        var breakdown = Strategy("DE", inclusive: false, OverThreshold).TaxFor(input);

        Assert.Equal(TaxScheme.Oss, breakdown.Scheme);
        Assert.Equal(TaxExemptionReason.OutsideEuVatArea, breakdown.Lines[0].ExemptionReason);
        Assert.Equal(new LineTax(TaxCategory.Standard, 2_000, 5_000, 1_000), breakdown.Lines[1]);
        Assert.Equal(new LineTax(TaxCategory.Exempt, 0, 700, 0, TaxExemptionReason.OutsideEuVatArea), breakdown.Shipping);
        Assert.Equal(["ES", "FR"], breakdown.LineDestinations!);
    }

    [Fact]
    public void Goods_to_Northern_Ireland_are_an_OSS_distance_sale_at_the_UK_rate_and_services_follow_the_UK()
    {
        var goods = Cart("GB", null, "BT1 1AA", Goods(10_000)) with { ShippingMinor = 500 };
        var over = Strategy("DE", inclusive: false, OverThreshold).TaxFor(goods);
        Assert.Equal(("XI", TaxScheme.Oss, 2_100L), (over.SaleDestination, over.Scheme, over.TaxMinor)); // 20 % of 10500

        var under = Strategy("DE", inclusive: false, EuDistanceSalesTurnover.None).TaxFor(goods);
        Assert.Equal(("XI", TaxScheme.Domestic, 1_995L), (under.SaleDestination, under.Scheme, under.TaxMinor)); // 19 % at home
        Assert.True(TaxStrategySelector.NeedsDistanceSalesTurnover(Copy("DE"), goods));

        // An e-book billed to Belfast is supplied in the UK: outside the EU — the home rate, as for any non-EU customer.
        var service = new TaxCalculationInput("EUR", "GB", [Digital(10_000)], BillCountry: "GB", BillPostcode: "BT1 1AA");
        var serviceTax = Strategy("DE", inclusive: false, OverThreshold).TaxFor(service);
        Assert.Equal(("GB", TaxScheme.Domestic, 1_900L), (serviceTax.SaleDestination, serviceTax.Scheme, serviceTax.TaxMinor));
        Assert.False(TaxStrategySelector.NeedsDistanceSalesTurnover(Copy("DE"), service));
    }

    [Fact]
    public void An_export_never_needs_or_counts_toward_the_threshold()
    {
        Assert.False(TaxStrategySelector.NeedsDistanceSalesTurnover(Copy("DE"), Cart("ES", null, "35001", Goods(1))));
        Assert.True(TaxStrategySelector.NeedsDistanceSalesTurnover(Copy("DE"), Cart("ES", null, "28013", Goods(1))));

        var canaries = Order("ES", [Line("ES", 10_000, TaxExemptionReason.OutsideEuVatArea)], TaxExemptionReason.OutsideEuVatArea);
        Assert.Empty(EuDistanceSale.For(canaries, Now, Now));

        var mixed = Order("ES", [Line("ES", 10_000, TaxExemptionReason.OutsideEuVatArea), Line("FR", 5_000)], TaxExemptionReason.OutsideEuVatArea);
        Assert.Equal([("FR", 5_000L)], EuDistanceSale.For(mixed, Now, Now).Select(r => (r.DestinationCountry, r.NetMinor)));

        var belfast = Order("XI", [Line("XI", 10_000)]);
        Assert.Equal([("XI", 10_420L)], EuDistanceSale.For(belfast, Now, Now).Select(r => (r.DestinationCountry, r.NetMinor)));

        // Negative control: a mainland Spanish sale from a German store still counts.
        Assert.Equal([("ES", 10_420L)], EuDistanceSale.For(Order("ES", [Line("ES", 10_000)]), Now, Now)
            .Select(r => (r.DestinationCountry, r.NetMinor)));
    }

    [Fact]
    public void Non_EU_storefronts_are_unchanged_by_the_territorial_rules()
    {
        foreach (var jurisdiction in new[] { "", "AU", "US-CA", "GB" })
        {
            var copy = Copy(jurisdiction);
            var input = Cart("ES", "ES-CN", "35001", Goods(10_000), Digital(3_000)) with { ShippingMinor = 499, BillCountry = "GB", BillPostcode = "BT1 1AA" };
            var actual = TaxStrategySelector.For(copy, OverThreshold, Now).TaxFor(input);
            var expected = new StorefrontRateTaxStrategy(copy.TaxRateBasisPoints, copy.TaxInclusive).TaxFor(input);
            Assert.Equal(expected.Lines, actual.Lines);
            Assert.Equal((expected.Shipping, expected.Scheme, expected.TaxMinor), (actual.Shipping, actual.Scheme, actual.TaxMinor));
            Assert.Null(actual.LineDestinations);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static EuOssTaxStrategy Strategy(string home, bool inclusive, EuDistanceSalesTurnover turnover) =>
        new(home, EuOssTaxStrategyTests.StandardRates2026[home], inclusive, EuVatRateTable.Current, turnover, Now);

    private static TaxLineInput Goods(long amount, bool exempt = false) =>
        new(amount, 0, exempt ? TaxExemptionReason.DestinationShipRule : TaxExemptionReason.None, Fulfilment: FulfilmentType.Warehouse);

    private static TaxLineInput Digital(long amount) => new(amount, 0, Fulfilment: FulfilmentType.DigitalDownload);

    private static TaxCalculationInput Cart(string shipTo, string? region, string? postcode, params TaxLineInput[] lines) =>
        new("EUR", shipTo, lines, ShipRegion: region, ShipPostcode: postcode);

    private static StorefrontTaxCopy Copy(string jurisdiction) => new()
    {
        StorefrontId = Guid.NewGuid(),
        Currency = "EUR",
        TaxRateBasisPoints = 1_900,
        TaxInclusive = false,
        IsLive = true,
        TaxJurisdiction = jurisdiction,
    };

    private static OrderLine Line(string destination, long net, TaxExemptionReason reason = TaxExemptionReason.None) =>
        new() { Title = "L", Quantity = 1, UnitPriceMinor = net, TaxableBaseMinor = net, TaxDestinationCountry = destination, ExemptionReason = reason };

    private static Order Order(string destination, OrderLine[] lines, TaxExemptionReason shippingReason = TaxExemptionReason.None) => new()
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
        TaxJurisdiction = "DE",
        TaxDestinationCountry = destination,
        ShippingTaxableBaseMinor = 420,
        ShippingExemptionReason = shippingReason,
        Lines = [.. lines],
    };
}
