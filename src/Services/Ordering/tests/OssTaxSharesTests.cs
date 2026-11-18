using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// ADR-0063 § 16, per-destination OSS tax accounts: checkout tells Payments which part of the tax was charged under OSS,
/// by Member State of consumption (<see cref="OssTaxShares"/>), so each destination's VAT is posted to its own liability
/// account. Exactly the components the strategy taxed at a destination's rate count — each line at its own destination,
/// the shipping at the sale's — and a home-country component, an export or any non-OSS sale counts nothing.
/// </summary>
public class OssTaxSharesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly EuDistanceSalesTurnover Over = new(EuOssTaxStrategy.ThresholdMinorEur + 1, 0);

    [Fact]
    public void A_mixed_oss_sale_reports_each_destinations_tax_with_the_shipping_on_the_goods()
    {
        // Goods to Austria (20 %) + 600 shipping, an e-book billed to Hungary (27 %): AT 2000 + 100, HU 2700 — all of it.
        var input = Cart("AT", "HU", Line(12_000), Line(12_700, FulfilmentType.DigitalDownload)) with { ShippingMinor = 600 };
        var tax = Strategy(Over).TaxFor(input);

        var shares = OssTaxShares.Of(tax, "DE-BY", "AT");

        Assert.Equal([("AT", 2_100L), ("HU", 2_700L)], shares);
        Assert.Equal(tax.TaxMinor, shares.Sum(s => s.TaxMinor));
    }

    [Fact]
    public void Home_country_components_stay_domestic()
    {
        // Goods shipped home (German VAT) + an e-book billed to France under OSS: only France's VAT is OSS.
        var tax = Strategy(Over).TaxFor(Cart("DE", "FR", Line(11_900), Line(12_000, FulfilmentType.DigitalDownload)) with { ShippingMinor = 595 });

        var shares = OssTaxShares.Of(tax, "DE", "DE");

        Assert.Equal([("FR", 2_000L)], shares);
        Assert.True(shares.Sum(s => s.TaxMinor) < tax.TaxMinor); // the German part stays on the store's own account
    }

    [Fact]
    public void Northern_Ireland_goods_are_oss_under_xi()
    {
        var tax = Strategy(Over).TaxFor(Cart("GB", null, Line(12_000)) with { ShipRegion = "GB-NIR" });
        Assert.Equal([("XI", tax.TaxMinor)], OssTaxShares.Of(tax, "DE", "GB"));
    }

    [Fact]
    public void A_sale_with_no_oss_part_reports_none()
    {
        // Under the threshold: a cross-border sale at the home rate (Domestic).
        Assert.Empty(OssTaxShares.Of(Strategy(EuDistanceSalesTurnover.None).TaxFor(Cart("FR", null, Line(11_900))), "DE", "FR"));
        // An export to the Canaries: zero-rated.
        Assert.Empty(OssTaxShares.Of(Strategy(Over).TaxFor(Cart("ES", null, Line(10_000)) with { ShipPostcode = "35001" }), "DE", "ES"));
        // A non-EU store's single rate.
        var us = new StorefrontRateTaxStrategy(825, false).TaxFor(Cart("US", null, Line(10_000)));
        Assert.Empty(OssTaxShares.Of(us, "US-CA", "US"));
        // Negative control: the same French sale over the threshold IS OSS.
        Assert.NotEmpty(OssTaxShares.Of(Strategy(Over).TaxFor(Cart("FR", null, Line(11_900))), "DE", "FR"));
    }

    [Fact]
    public void The_shares_never_exceed_the_tax_over_random_carts()
    {
        var random = new Random(20261118);
        string[] destinations = ["DE", "FR", "AT", "IT", "HU", "US", "XI", "ES"];
        FulfilmentType[] types = [FulfilmentType.Warehouse, FulfilmentType.DigitalDownload, FulfilmentType.Subscription];
        for (var i = 0; i < 3_000; i++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 5))
                .Select(_ => Line(random.Next(1, 50_000), types[random.Next(types.Length)], exempt: random.Next(10) == 0)).ToArray();
            var input = Cart(destinations[random.Next(destinations.Length)], destinations[random.Next(destinations.Length)], lines)
                with { ShippingMinor = random.Next(0, 2_000) };
            var tax = Strategy(Over).TaxFor(input);

            var shares = OssTaxShares.Of(tax, "DE", input.ShipCountry!);

            Assert.True(shares.Sum(s => s.TaxMinor) <= tax.TaxMinor);
            Assert.All(shares, s => Assert.True(s.TaxMinor > 0 && s.Country != "DE"));
            Assert.True(tax.Scheme == TaxScheme.Oss || shares.Count == 0);
        }
    }

    private static EuOssTaxStrategy Strategy(EuDistanceSalesTurnover turnover) =>
        new("DE", 1_900, true, EuVatRateTable.Current, turnover, Now);

    private static TaxLineInput Line(long amount, FulfilmentType type = FulfilmentType.Warehouse, bool exempt = false) =>
        new(amount, 0, exempt ? TaxExemptionReason.DestinationShipRule : TaxExemptionReason.None, Fulfilment: type);

    private static TaxCalculationInput Cart(string shipTo, string? billTo, params TaxLineInput[] lines) =>
        new("EUR", shipTo, lines, BillCountry: billTo);
}
