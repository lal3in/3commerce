using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.Identity.Tests;

/// <summary>
/// ADR-0063 P3b: the member's saved default business identity. Validated with the same per-scheme rules checkout
/// applies to its business-purchase block (the format copy is pinned against Ordering's and Entity's by
/// <c>BuyerTaxIdFormatParityTests</c>); a refusal names the input at fault.
/// </summary>
public class BusinessIdentityTests
{
    [Fact]
    public void A_company_without_a_tax_id_is_kept_trimmed()
    {
        var identity = BusinessIdentityRules.Resolve("  Acme Pty Ltd ", null, null, null);

        Assert.Equal(new BusinessIdentity("Acme Pty Ltd", null, null, null), identity);
    }

    [Theory]
    [InlineData(TaxIdScheme.EuVat, "de 123.456-789", null, "DE123456789", "DE")]
    [InlineData(TaxIdScheme.EuVat, "EL123456789", null, "EL123456789", "GR")]
    [InlineData(TaxIdScheme.Abn, "51 824 753 556", null, "51824753556", "AU")]
    [InlineData(TaxIdScheme.GbVrn, "123 4567 89", null, "GB123456789", "GB")]
    [InlineData(TaxIdScheme.CaBn, "123456789 RT 0001", "ca-qc", "123456789RT0001", "CA-QC")]
    [InlineData(TaxIdScheme.UsStateSalesTaxId, "sr ab 12-345678", "us-ca", "SRAB12-345678", "US-CA")]
    public void A_tax_id_is_normalized_per_scheme_with_its_jurisdiction(
        TaxIdScheme scheme, string taxId, string? jurisdiction, string expectedTaxId, string expectedJurisdiction)
    {
        var identity = BusinessIdentityRules.Resolve("Acme", taxId, scheme, jurisdiction);

        Assert.Equal(expectedTaxId, identity.TaxId);
        Assert.Equal(scheme, identity.TaxIdScheme);
        Assert.Equal(expectedJurisdiction, identity.TaxIdJurisdiction);
    }

    [Theory]
    [InlineData(null, null, null, null, BusinessIdentityRules.CompanyNameField)]
    [InlineData("   ", null, null, null, BusinessIdentityRules.CompanyNameField)]
    [InlineData("Acme", "DE123456789", null, null, BusinessIdentityRules.TaxIdSchemeField)]
    [InlineData("Acme", null, TaxIdScheme.EuVat, null, BusinessIdentityRules.TaxIdField)]
    [InlineData("Acme", "DE12345678", TaxIdScheme.EuVat, null, BusinessIdentityRules.TaxIdField)]
    [InlineData("Acme", "GR123456789", TaxIdScheme.EuVat, null, BusinessIdentityRules.TaxIdField)]
    [InlineData("Acme", "DE123456789", TaxIdScheme.EuVat, "FR", BusinessIdentityRules.TaxIdJurisdictionField)]
    [InlineData("Acme", "1234567890", TaxIdScheme.Abn, null, BusinessIdentityRules.TaxIdField)]
    [InlineData("Acme", "51824753556", TaxIdScheme.Abn, "NZ", BusinessIdentityRules.TaxIdJurisdictionField)]
    [InlineData("Acme", "SR-1234", TaxIdScheme.UsStateSalesTaxId, null, BusinessIdentityRules.TaxIdJurisdictionField)]
    [InlineData("Acme", "SR-1234", TaxIdScheme.UsStateSalesTaxId, "US-ZZ", BusinessIdentityRules.TaxIdJurisdictionField)]
    [InlineData("Acme", "DE123456789", (TaxIdScheme)42, null, BusinessIdentityRules.TaxIdSchemeField)]
    public void A_refusal_names_the_field_at_fault(
        string? companyName, string? taxId, TaxIdScheme? scheme, string? jurisdiction, string field)
    {
        var ex = Assert.Throws<BusinessIdentityException>(() =>
            BusinessIdentityRules.Resolve(companyName, taxId, scheme, jurisdiction));

        Assert.Equal(field, ex.Field);
    }

    [Fact]
    public void Values_beyond_the_column_bounds_are_refused_not_truncated()
    {
        Assert.Equal(BusinessIdentityRules.CompanyNameField, Assert.Throws<BusinessIdentityException>(() =>
            BusinessIdentityRules.Resolve(new string('a', BusinessIdentityRules.CompanyNameMaxLength + 1), null, null, null)).Field);
        Assert.Equal(BusinessIdentityRules.TaxIdField, Assert.Throws<BusinessIdentityException>(() =>
            BusinessIdentityRules.Resolve("Acme", new string('9', BusinessIdentityRules.TaxIdMaxLength + 1), TaxIdScheme.OtherTaxRegistration, null)).Field);

        // The longest accepted company name fits the column exactly.
        Assert.Equal(BusinessIdentityRules.CompanyNameMaxLength,
            BusinessIdentityRules.Resolve(new string('a', BusinessIdentityRules.CompanyNameMaxLength), null, null, null).CompanyName.Length);
    }

    [Fact]
    public void A_member_saves_and_clears_the_default_business_identity()
    {
        var user = new User { Id = Guid.CreateVersion7(), Email = "buyer@example.test", PasswordHash = "hash" };
        Assert.Null(user.BusinessIdentity);

        var identity = BusinessIdentityRules.Resolve("Acme GmbH", "DE123456789", TaxIdScheme.EuVat, null);
        user.SetBusinessIdentity(identity);

        Assert.Equal(identity, user.BusinessIdentity);
        Assert.Equal("DE", user.BusinessTaxIdJurisdiction);

        user.SetBusinessIdentity(null);

        Assert.Null(user.BusinessIdentity);
        Assert.Null(user.BusinessCompanyName);
        Assert.Null(user.BusinessTaxId);
        Assert.Null(user.BusinessTaxIdScheme);
        Assert.Null(user.BusinessTaxIdJurisdiction);
    }
}
