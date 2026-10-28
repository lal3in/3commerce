using System.Text.Json;
using MassTransit.Serialization;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure.Projections;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane (ADR-0055 back-compat pattern, ADR-0063 P2): a StorefrontConfigChanged published BEFORE the
/// contract carried TaxJurisdiction / SellerEstablishmentKey must still deserialize — with MassTransit's own
/// serializer options — and project onto Ordering's StorefrontTaxCopy with the defaults ("").
/// </summary>
public class StorefrontConfigBackCompatTests
{
    private static readonly Guid StorefrontId = Guid.Parse("01920000-0000-7000-8000-000000000001");
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    // Exactly the shape a pre-P2 Catalog published: every field up to DiscountBps, nothing after.
    private const string PreP2Json = """
        {
          "storefrontId": "01920000-0000-7000-8000-000000000001",
          "tenantId": "00000000-0000-0000-0000-000000000001",
          "name": "Legacy AU",
          "currency": "AUD",
          "taxRateBasisPoints": 1000,
          "isLive": true,
          "taxInclusive": true,
          "receivableAccountCode": "receivable.au",
          "revenueAccountCode": "revenue.au",
          "taxAccountCode": "tax.au",
          "shippingAccountCode": "shipping.au",
          "shipToCountries": ["AU"],
          "discountBps": 0
        }
        """;

    [Fact]
    public void A_pre_p2_message_deserializes_with_default_tax_identity_and_still_projects()
    {
        var message = JsonSerializer.Deserialize<StorefrontConfigChanged>(PreP2Json, SystemTextJsonMessageSerializer.Options)!;

        Assert.Equal(string.Empty, message.TaxJurisdiction);
        Assert.Equal(string.Empty, message.SellerEstablishmentKey);

        var copy = new StorefrontTaxCopy { StorefrontId = StorefrontId, Currency = "XXX" };
        StorefrontConfigConsumer.Apply(copy, message);

        // Everything the old message carried still applies...
        Assert.Equal((TenantId, "AUD", 1000, true, true), (copy.TenantId, copy.Currency, copy.TaxRateBasisPoints, copy.IsLive, copy.TaxInclusive));
        Assert.Equal(["AU"], copy.ShipToCountries);
        // ...and the new fields take their defaults.
        Assert.Equal(string.Empty, copy.TaxJurisdiction);
        Assert.Equal(string.Empty, copy.SellerEstablishmentKey);
    }

    [Fact]
    public void A_p2_message_round_trips_and_projects_the_tax_identity()
    {
        // Positive control: the same path DOES carry the new fields when they are sent, so the test above
        // would notice if the projection dropped them.
        var sent = new StorefrontConfigChanged(
            StorefrontId, TenantId, "New AU", "AUD", 1000, IsLive: true, TaxInclusive: true,
            TaxJurisdiction: "AU", SellerEstablishmentKey: "sydney-hq");
        var json = JsonSerializer.Serialize(sent, SystemTextJsonMessageSerializer.Options);
        var received = JsonSerializer.Deserialize<StorefrontConfigChanged>(json, SystemTextJsonMessageSerializer.Options)!;

        var copy = new StorefrontTaxCopy { StorefrontId = StorefrontId, Currency = "AUD" };
        StorefrontConfigConsumer.Apply(copy, received);

        Assert.Equal("AU", copy.TaxJurisdiction);
        Assert.Equal("sydney-hq", copy.SellerEstablishmentKey);
    }
}
