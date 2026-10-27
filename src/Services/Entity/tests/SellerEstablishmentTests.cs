using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Tests;

/// <summary>ADR-0063 P2: the seller of record and its establishments (domain rules).</summary>
public class SellerEstablishmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    private static SellerEstablishmentDetails Sydney(string? jurisdiction = null, string country = "AU") =>
        new("Sydney HQ", "1 George St", null, "Sydney", "NSW", "2000", country, jurisdiction);

    private static EntityRecord Seller()
    {
        var entity = EntityRecord.Create(Guid.NewGuid(), EntityType.Company, "Seller Pty Ltd", null, Now, []);
        entity.DesignateAsSeller(Now);
        return entity;
    }

    [Fact]
    public void Designation_makes_the_entity_the_seller_and_is_idempotent()
    {
        var entity = EntityRecord.Create(Guid.NewGuid(), EntityType.Company, "Seller Pty Ltd", null, Now, []);
        Assert.False(entity.IsSeller);

        var first = entity.DesignateAsSeller(Now);
        var second = entity.DesignateAsSeller(Now);

        Assert.True(entity.IsSeller);
        Assert.Same(first, second);
        Assert.Single(entity.Profiles, p => p.Role == EntityRoleKind.TenantOwner);
    }

    [Fact]
    public void A_natural_person_or_archived_entity_cannot_be_the_seller()
    {
        var person = EntityRecord.Create(Guid.NewGuid(), EntityType.NaturalPerson, "Jane Doe", null, Now, []);
        Assert.Throws<DomainRuleException>(() => person.DesignateAsSeller(Now));

        var archived = EntityRecord.Create(Guid.NewGuid(), EntityType.Company, "Gone Ltd", null, Now, []);
        archived.Archive(Now);
        Assert.Throws<DomainRuleException>(() => archived.DesignateAsSeller(Now));
        Assert.False(archived.IsSeller);
    }

    [Fact]
    public void Establishment_defaults_its_jurisdiction_to_the_address_country()
    {
        var seller = Seller();

        var establishment = seller.AddEstablishment(" Sydney-HQ ", Sydney(), Now);

        Assert.Equal("sydney-hq", establishment.Key);
        Assert.Equal("AU", establishment.Jurisdiction);
        Assert.Equal("AU", establishment.CountryCode);
        Assert.Equal(seller.Id, establishment.EntityId);
    }

    [Fact]
    public void Establishment_jurisdiction_may_narrow_to_a_region_of_its_country_only()
    {
        var seller = Seller();
        var la = seller.AddEstablishment("la", Sydney("us-ca", "us"), Now);
        Assert.Equal("US-CA", la.Jurisdiction);

        // Negative control: a jurisdiction in another country than the address is rejected, naming the field.
        var ex = Assert.Throws<DomainRuleException>(() => seller.AddEstablishment("berlin", Sydney("DE", "AU"), Now));
        Assert.Equal("Jurisdiction", ex.Field);
        var bad = Assert.Throws<DomainRuleException>(() => seller.AddEstablishment("berlin", Sydney("Germany", "DE"), Now));
        Assert.Equal("Jurisdiction", bad.Field);
    }

    [Theory]
    [InlineData("a", "Key")]
    [InlineData("-lead", "Key")]
    [InlineData("has space", "Key")]
    public void Establishment_key_must_be_a_slug(string key, string field)
    {
        var ex = Assert.Throws<DomainRuleException>(() => Seller().AddEstablishment(key, Sydney(), Now));
        Assert.Equal(field, ex.Field);
    }

    [Fact]
    public void Establishment_key_is_unique_per_seller()
    {
        var seller = Seller();
        seller.AddEstablishment("sydney-hq", Sydney(), Now);

        var ex = Assert.Throws<DomainRuleException>(() => seller.AddEstablishment("SYDNEY-HQ", Sydney(), Now));
        Assert.Equal("Key", ex.Field);
        Assert.Single(seller.Establishments);
    }

    [Theory]
    [InlineData("", "Line1", "Sydney", "2000", "AU", "Label")]
    [InlineData("HQ", "", "Sydney", "2000", "AU", "Line1")]
    [InlineData("HQ", "1 George St", "", "2000", "AU", "City")]
    [InlineData("HQ", "1 George St", "Sydney", "", "AU", "Postcode")]
    [InlineData("HQ", "1 George St", "Sydney", "2000", "Australia", "CountryCode")]
    public void Establishment_address_fields_are_validated(string label, string line1, string city, string postcode, string country, string field)
    {
        var ex = Assert.Throws<DomainRuleException>(() =>
            Seller().AddEstablishment("hq", new(label, line1, null, city, null, postcode, country, null), Now));
        Assert.Equal(field, ex.Field);
    }

    [Fact]
    public void Only_the_seller_has_establishments()
    {
        var supplier = EntityRecord.Create(Guid.NewGuid(), EntityType.Company, "Supplier Ltd", null, Now, [EntityRoleKind.Supplier]);

        Assert.Throws<DomainRuleException>(() => supplier.AddEstablishment("hq", Sydney(), Now));
        Assert.Empty(supplier.Establishments);
    }

    [Fact]
    public void Update_edits_the_address_and_jurisdiction_but_never_the_key()
    {
        var seller = Seller();
        var establishment = seller.AddEstablishment("hq", Sydney(), Now);

        seller.UpdateEstablishment(establishment.Id, new("Auckland HQ", "2 Queen St", null, "Auckland", null, "1010", "NZ", null), Now.AddDays(1));

        Assert.Equal("hq", establishment.Key);
        Assert.Equal("Auckland HQ", establishment.Label);
        Assert.Equal("NZ", establishment.Jurisdiction);
        Assert.Equal(Now.AddDays(1), establishment.UpdatedAt);
        Assert.Throws<DomainRuleException>(() => seller.UpdateEstablishment(Guid.NewGuid(), Sydney(), Now));
    }
}
