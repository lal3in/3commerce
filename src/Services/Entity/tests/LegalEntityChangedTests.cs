using ThreeCommerce.BuildingBlocks.Contracts.Entity;
using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.Entity.Api.Endpoints;
using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Tests;

/// <summary>ADR-0063 P2: the LegalEntityChanged snapshot and the "seller only" publish rule.</summary>
public class LegalEntityChangedTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_seller_publishes_a_full_snapshot()
    {
        var seller = EntityRecord.Create(Guid.NewGuid(), EntityType.Company, "Seller GmbH", "Seller", Now, []);
        seller.DesignateAsSeller(Now);
        var vat = seller.AddIdentifier(EntityIdentifierType.EuVat, "DE123456789", Now);
        seller.MarkIdentifierVerified(vat.Id, Now);
        seller.AddIdentifier(EntityIdentifierType.UsStateSalesTaxId, "123-456", Now, "US-NY");
        seller.AddAddress(EntityAddressPurpose.RegisteredOffice, "Unter den Linden 1", null, "Berlin", null, "10117", "DE", Now);
        seller.AddEstablishment("berlin", new("Berlin", "Unter den Linden 1", null, "Berlin", null, "10117", "DE", null), Now);
        var publish = new NoopPublishEndpoint();

        await LegalEntityEvents.PublishIfSellerAsync(publish, seller, Now, default);

        var message = Assert.IsType<LegalEntityChanged>(Assert.Single(publish.Published));
        Assert.Equal(seller.TenantId, message.TenantId);
        Assert.Equal(seller.Id, message.EntityId);
        Assert.Equal("Seller GmbH", message.LegalName);
        Assert.Equal("Seller", message.TradingName);
        Assert.True(message.IsSeller);
        Assert.Equal(Now, message.OccurredAt);
        var eu = Assert.Single(message.TaxIdentifiers, i => i.Scheme == TaxIdScheme.EuVat);
        Assert.Equal(("DE123456789", "DE", true), (eu.Value, eu.Jurisdiction, eu.Verified));
        var us = Assert.Single(message.TaxIdentifiers, i => i.Scheme == TaxIdScheme.UsStateSalesTaxId);
        Assert.Equal(("123-456", "US-NY", false), (us.Value, us.Jurisdiction, us.Verified));
        var establishment = Assert.Single(message.Establishments);
        Assert.Equal(("berlin", "DE", "Berlin"), (establishment.Key, establishment.Jurisdiction, establishment.Address.City));
        Assert.Equal("10117", message.RegisteredOffice!.Postcode);
    }

    [Fact]
    public async Task A_non_seller_publishes_nothing()
    {
        // Negative control: the same identity changes on a supplier (no TenantOwner profile) are not seller identity.
        var supplier = EntityRecord.Create(Guid.NewGuid(), EntityType.Company, "Supplier Ltd", null, Now, [EntityRoleKind.Supplier]);
        supplier.AddIdentifier(EntityIdentifierType.EuVat, "FRAB123456789", Now);
        var publish = new NoopPublishEndpoint();

        await LegalEntityEvents.PublishIfSellerAsync(publish, supplier, Now, default);

        Assert.Empty(publish.Published);
    }

    [Fact]
    public void Tax_id_scheme_contract_mirrors_the_entity_identifier_types()
    {
        // TaxIdScheme (BuildingBlocks.Contracts) is cast from EntityIdentifierType when publishing; the two
        // must agree member for member, value for value, or a consumer would read the wrong scheme.
        var domain = Enum.GetValues<EntityIdentifierType>().ToDictionary(v => v.ToString(), v => (int)v);
        var contract = Enum.GetValues<TaxIdScheme>().ToDictionary(v => v.ToString(), v => (int)v);

        Assert.Equal(domain.OrderBy(p => p.Key), contract.OrderBy(p => p.Key));
    }
}
