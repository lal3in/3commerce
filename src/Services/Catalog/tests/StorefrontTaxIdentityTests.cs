using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

/// <summary>ADR-0063 P2: a storefront's tax jurisdiction and seller-establishment binding.</summary>
public class StorefrontTaxIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    private static Storefront NewStorefront() => Storefront.Create(Guid.CreateVersion7(), "AU store", Now);

    [Fact]
    public void A_new_storefront_has_no_tax_identity_yet()
    {
        var storefront = NewStorefront();

        Assert.Equal(string.Empty, storefront.TaxJurisdiction);
        Assert.Equal(string.Empty, storefront.SellerEstablishmentKey);
    }

    [Theory]
    [InlineData(" au ", "AU")]
    [InlineData("us-ca", "US-CA")]
    [InlineData("GB-NIR", "GB-NIR")]
    public void Jurisdiction_is_normalized(string input, string expected)
    {
        var storefront = NewStorefront();

        storefront.SetTaxIdentity(input, " Sydney-HQ ", Now);

        Assert.Equal(expected, storefront.TaxJurisdiction);
        Assert.Equal("sydney-hq", storefront.SellerEstablishmentKey);
    }

    [Theory]
    [InlineData("Australia", null, "TaxJurisdiction")]
    [InlineData("A", null, "TaxJurisdiction")]
    [InlineData("US-CALI", null, "TaxJurisdiction")]
    [InlineData(null, "has space", "SellerEstablishmentKey")]
    [InlineData(null, "-x", "SellerEstablishmentKey")]
    public void Invalid_values_are_rejected_naming_the_field(string? jurisdiction, string? key, string field)
    {
        var storefront = NewStorefront();

        var ex = Assert.Throws<CatalogRuleException>(() => storefront.SetTaxIdentity(jurisdiction, key, Now));

        Assert.Equal(field, ex.Field);
        Assert.Equal(string.Empty, storefront.TaxJurisdiction);
    }

    [Fact]
    public void Null_leaves_the_value_and_empty_clears_it()
    {
        var storefront = NewStorefront();
        storefront.SetTaxIdentity("AU", "sydney-hq", Now);

        storefront.SetTaxIdentity(null, null, Now); // an older admin client that doesn't send the fields
        Assert.Equal(("AU", "sydney-hq"), (storefront.TaxJurisdiction, storefront.SellerEstablishmentKey));

        storefront.SetTaxIdentity("", "", Now);
        Assert.Equal((string.Empty, string.Empty), (storefront.TaxJurisdiction, storefront.SellerEstablishmentKey));
    }

    [Fact]
    public void Duplication_copies_the_jurisdiction_and_establishment()
    {
        var source = NewStorefront();
        source.ConfigureCommerce("http://localhost:3000/au", "AUD", StorefrontTaxRegime.AuGst, 1000, Now);
        source.SetTaxIdentity("AU", "sydney-hq", Now);

        var clone = Storefront.DuplicateFrom(source, "AU store copy", Now);

        Assert.Equal("AU", clone.TaxJurisdiction);
        Assert.Equal("sydney-hq", clone.SellerEstablishmentKey);

        // Negative control: a source without a tax identity clones to none (nothing is invented).
        var bare = Storefront.DuplicateFrom(NewStorefront(), "Bare copy", Now);
        Assert.Equal((string.Empty, string.Empty), (bare.TaxJurisdiction, bare.SellerEstablishmentKey));
    }
}
