using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// ADR-0063 § 16, OSS threshold crossing: a settled renewal of a subscription line supplied into ANOTHER Member State is a
/// new distance sale (each period is a new supply, § 7.1), so it adds its own row to the seller's running total, keyed by
/// the renewal's ledger reference — <see cref="EuDistanceSale.ForRenewal"/>. Home-country, non-EU, exported and non-EU-store
/// renewals add nothing.
/// </summary>
public class RenewalDistanceSaleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_cross_border_renewal_counts_under_its_lines_destination_with_its_own_reference()
    {
        var line = LineAt("fr");
        var order = Order("DE-BY", "DE", line);

        var row = EuDistanceSale.ForRenewal(order, line, "renew-x-2", "eur", 1_000, TaxScheme.Domestic, Now, Now);

        Assert.NotNull(row);
        Assert.Equal(
            (order.Id, order.TenantId, "DE", "FR", "renew-x-2", "EUR", 1_000L, 2026, TaxScheme.Domestic),
            (row!.OrderId, row.TenantId, row.HomeCountry, row.DestinationCountry, row.SourceReference, row.Currency, row.NetMinor, row.Year, row.TaxScheme));
    }

    [Fact]
    public void A_line_recorded_before_per_line_destinations_takes_the_orders_destination()
    {
        var line = LineAt("");
        Assert.Equal("AT", EuDistanceSale.ForRenewal(Order("DE", "AT", line), line, "renew-x-2", "EUR", 500, TaxScheme.Oss, Now, Now)?.DestinationCountry);
    }

    [Theory]
    [InlineData("DE", "DE", TaxScheme.Domestic, 1_000)]   // a home-country supply
    [InlineData("DE", "GB", TaxScheme.Domestic, 1_000)]   // outside the EU (a service to Northern Ireland follows the UK)
    [InlineData("DE", "ES", TaxScheme.Export, 1_000)]     // exported from the EU VAT area (the Canaries)
    [InlineData("US-CA", "FR", TaxScheme.Domestic, 1_000)] // a non-EU store
    [InlineData("", "FR", TaxScheme.Domestic, 1_000)]     // an order before P1b recorded no jurisdiction
    [InlineData("DE", "FR", TaxScheme.Oss, 0)]            // nothing net
    public void Renewals_that_are_not_distance_sales_add_nothing(string jurisdiction, string destination, TaxScheme scheme, long net)
    {
        var line = LineAt(destination);
        Assert.Null(EuDistanceSale.ForRenewal(Order(jurisdiction, destination, line), line, "renew-x-2", "EUR", net, scheme, Now, Now));
    }

    [Fact]
    public void A_line_zero_rated_as_an_export_at_checkout_adds_nothing_whatever_the_scheme_says()
    {
        var line = new OrderLine
        {
            Title = "S", Quantity = 1, UnitPriceMinor = 1_000, TaxableBaseMinor = 1_000, TaxDestinationCountry = "ES",
            ExemptionReason = TaxExemptionReason.OutsideEuVatArea,
        };
        Assert.Null(EuDistanceSale.ForRenewal(Order("DE", "ES", line), line, "renew-x-2", "EUR", 1_000, TaxScheme.Domestic, Now, Now));

        // Negative control: the same line, not exported, counts.
        var counted = LineAt("ES");
        Assert.NotNull(EuDistanceSale.ForRenewal(Order("DE", "ES", counted), counted, "renew-x-2", "EUR", 1_000, TaxScheme.Domestic, Now, Now));
    }

    private static OrderLine LineAt(string destination) =>
        new() { Title = "S", Quantity = 1, UnitPriceMinor = 1_000, TaxableBaseMinor = 1_000, TaxDestinationCountry = destination };

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
        TaxScheme = TaxScheme.Domestic,
        TaxJurisdiction = jurisdiction,
        TaxDestinationCountry = destination,
        Lines = [.. lines],
    };
}
