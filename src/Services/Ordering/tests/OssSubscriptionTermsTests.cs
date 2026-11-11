using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure.Consumers;
using Facts = ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// ADR-0063 § 16, OSS renewals: the subscription a recurring line sets up carries the scheme and place of supply of THAT
/// LINE (owner decision 2026-11-06; the per-line destination of #329) — the line's own destination, the order's for a
/// line written before per-line destinations — and is Oss only when the sale was OSS and the line went to a Member State
/// other than the storefront's home; a home-country line of a mixed OSS sale is Domestic; any other sale keeps its scheme.
/// </summary>
public class OssSubscriptionTermsTests
{
    [Theory]
    // order scheme, jurisdiction, order destination, line destination → scheme, destination
    [InlineData(TaxScheme.Oss, "DE", "FR", "FR", Facts.TaxScheme.Oss, "FR")]          // a cross-border OSS line
    [InlineData(TaxScheme.Oss, "DE-BY", "FR", "", Facts.TaxScheme.Oss, "FR")]          // a pre-#329 line: the order's destination
    [InlineData(TaxScheme.Oss, "DE", "AT", "HU", Facts.TaxScheme.Oss, "HU")]           // an e-service to its bill-to, goods elsewhere
    [InlineData(TaxScheme.Oss, "DE", "FR", "DE", Facts.TaxScheme.Domestic, "DE")]      // a home-country line of an OSS sale
    [InlineData(TaxScheme.Oss, "DE", "FR", "el", Facts.TaxScheme.Oss, "GR")]           // EL is Greece
    [InlineData(TaxScheme.Oss, "DE", "US", "US", Facts.TaxScheme.Domestic, "US")]      // outside the EU: never OSS
    [InlineData(TaxScheme.Domestic, "DE", "FR", "FR", Facts.TaxScheme.Domestic, "FR")] // under the threshold: home-taxed
    [InlineData(TaxScheme.NotRecorded, "", "", "", Facts.TaxScheme.NotRecorded, null)] // a pre-P1 order
    public void The_request_carries_the_lines_scheme_and_destination(
        TaxScheme orderScheme, string jurisdiction, string orderDestination, string lineDestination,
        Facts.TaxScheme expectedScheme, string? expectedDestination)
    {
        var order = Order(orderScheme, jurisdiction, orderDestination);
        var line = Line(lineDestination);

        var request = OrderStatusConsumer.SubscriptionRequestFor(order, line, taxInclusive: true, taxExempt: false);

        Assert.Equal((expectedScheme, expectedDestination), (request.TaxScheme, request.TaxDestinationCountry));
        Assert.Equal((int)order.LineTaxTerms(line).Scheme, (int)request.TaxScheme!.Value); // numeric value crosses unchanged
    }

    private static OrderLine Line(string destination) => new()
    {
        Id = Guid.CreateVersion7(),
        ProductId = Guid.CreateVersion7(),
        Title = "Plan",
        UnitPriceMinor = 1_200,
        Quantity = 1,
        BillingMode = BillingMode.Recurring,
        BillingPeriod = BillingPeriod.Monthly,
        TaxDestinationCountry = destination,
    };

    private static Order Order(TaxScheme scheme, string jurisdiction, string destination) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        StorefrontId = Guid.CreateVersion7(),
        Email = "subscriber@example.test",
        Status = OrderStatus.Confirmed,
        Currency = "EUR",
        ShipName = "S",
        ShipLine1 = "1 St",
        ShipCity = "C",
        ShipPostcode = "0000",
        ShipCountry = destination,
        TaxScheme = scheme,
        TaxJurisdiction = jurisdiction,
        TaxDestinationCountry = destination,
    };
}
