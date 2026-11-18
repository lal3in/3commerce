using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// ADR-0063 § 16, manual-service place of supply (owner decision 2026-11-15, final): a manual-service line (on-site
/// installation, assembly…) is taxed WHERE IT IS PERFORMED — the customer's address, the ship-to — never at the bill-to
/// and never at the seller's establishment (Art. 45). Pinned against an electronically supplied line, which goes to the
/// bill-to, as the negative control.
/// </summary>
public class ManualServicePlaceOfSupplyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly EuDistanceSalesTurnover Over = new(EuOssTaxStrategy.ThresholdMinorEur + 1, 0);

    [Fact]
    public void A_manual_service_is_supplied_at_the_ship_to_and_a_digital_one_at_the_bill_to()
    {
        var cart = Cart("DE", "FR");

        Assert.False(PlaceOfSupply.IsElectronicallySupplied(FulfilmentType.ManualService));
        Assert.Equal("DE", PlaceOfSupply.LineDestination(cart, Line(FulfilmentType.ManualService)));
        // Negative control: the same line as a digital download follows the bill-to.
        Assert.Equal("FR", PlaceOfSupply.LineDestination(cart, Line(FulfilmentType.DigitalDownload)));
    }

    [Fact]
    public void An_installation_billed_abroad_but_performed_at_home_is_taxed_at_home()
    {
        // A German store over the threshold: installed in Germany, billed to France → German VAT, Domestic, not France's.
        var tax = Strategy(Over).TaxFor(Cart("DE", "FR", Line(FulfilmentType.ManualService, 11_900)));

        Assert.Equal(["DE"], tax.LineDestinations!);
        Assert.Equal((1_900, TaxScheme.Domestic), (tax.Lines[0].RateBps, tax.Scheme));

        // Negative control: as an e-service the same line goes to France at France's 20 % under OSS.
        var digital = Strategy(Over).TaxFor(Cart("DE", "FR", Line(FulfilmentType.DigitalDownload, 11_900)));
        Assert.Equal(["FR"], digital.LineDestinations!);
        Assert.Equal((2_000, TaxScheme.Oss), (digital.Lines[0].RateBps, digital.Scheme));
    }

    [Fact]
    public void Performed_in_another_member_state_it_is_a_distance_sale_like_goods()
    {
        // Installed in France (ship-to), billed to Germany: France's rate under OSS once over the threshold; home rate under it.
        var over = Strategy(Over).TaxFor(Cart("FR", "DE", Line(FulfilmentType.ManualService, 12_000)));
        Assert.Equal(["FR"], over.LineDestinations!);
        Assert.Equal((2_000, TaxScheme.Oss), (over.Lines[0].RateBps, over.Scheme));

        var under = Strategy(EuDistanceSalesTurnover.None).TaxFor(Cart("FR", "DE", Line(FulfilmentType.ManualService, 11_900)));
        Assert.Equal((1_900, TaxScheme.Domestic), (under.Lines[0].RateBps, under.Scheme));
    }

    [Fact]
    public void In_Northern_Ireland_a_manual_service_follows_the_UK_and_only_goods_are_XI()
    {
        var cart = Cart("GB", null) with { ShipRegion = "GB-NIR", ShipPostcode = "BT1 1AA" };

        Assert.Equal("GB", PlaceOfSupply.LineDestination(cart, Line(FulfilmentType.ManualService)));
        Assert.Equal("XI", PlaceOfSupply.LineDestination(cart, Line(FulfilmentType.Warehouse))); // negative control: goods
    }

    private static EuOssTaxStrategy Strategy(EuDistanceSalesTurnover turnover) =>
        new("DE", 1_900, true, EuVatRateTable.Current, turnover, Now);

    private static TaxLineInput Line(FulfilmentType type, long amount = 10_000) =>
        new(amount, 0, TaxExemptionReason.None, Fulfilment: type);

    private static TaxCalculationInput Cart(string shipTo, string? billTo, params TaxLineInput[] lines) =>
        new("EUR", shipTo, lines, BillCountry: billTo);
}
