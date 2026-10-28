using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Tests;

/// <summary>
/// ADR-0063 P2: per-scheme tax-ID format validation. Each scheme has accepted shapes (positive) and rejected
/// shapes (negative control) whose error names the field at fault, so the endpoint's 400 can key it.
/// </summary>
public class TaxIdentifierFormatTests
{
    [Theory]
    [InlineData(EntityIdentifierType.EuVat, "DE 123 456 789", null, "DE123456789", "DE")]
    [InlineData(EntityIdentifierType.EuVat, "fr-ab-123456789", null, "FRAB123456789", "FR")]
    [InlineData(EntityIdentifierType.EuVat, "ATU12345678", null, "ATU12345678", "AT")]
    [InlineData(EntityIdentifierType.EuVat, "NL123456789B01", null, "NL123456789B01", "NL")]
    [InlineData(EntityIdentifierType.EuVat, "EL123456789", null, "EL123456789", "GR")]
    [InlineData(EntityIdentifierType.EuVat, "IE1234567T", "IE", "IE1234567T", "IE")]
    [InlineData(EntityIdentifierType.EuVat, "XI123456789", null, "XI123456789", "GB-NIR")]
    [InlineData(EntityIdentifierType.GbVrn, "GB 123 4567 89", null, "GB123456789", "GB")]
    [InlineData(EntityIdentifierType.GbVrn, "123456789", null, "GB123456789", "GB")]
    [InlineData(EntityIdentifierType.GbVrn, "GBGD001", null, "GBGD001", "GB")]
    [InlineData(EntityIdentifierType.NzGst, "123-456-789", null, "123456789", "NZ")]
    [InlineData(EntityIdentifierType.NzGst, "12345678", null, "12345678", "NZ")]
    [InlineData(EntityIdentifierType.CaBn, "123456789", null, "123456789", "CA")]
    [InlineData(EntityIdentifierType.CaBn, "123456789 RT 0001", "ca-qc", "123456789RT0001", "CA-QC")]
    [InlineData(EntityIdentifierType.ChUid, "CHE-123.456.789 MWST", null, "CHE123456789MWST", "CH")]
    [InlineData(EntityIdentifierType.ChUid, "che123456789", null, "CHE123456789", "CH")]
    [InlineData(EntityIdentifierType.UsStateSalesTaxId, "sr ab 12-345678", "us-ca", "SRAB12-345678", "US-CA")]
    [InlineData(EntityIdentifierType.Abn, "51 824 753 556", null, "51824753556", "AU")]
    [InlineData(EntityIdentifierType.Acn, "123 456 789", null, "123456789", "AU")]
    [InlineData(EntityIdentifierType.Gst, "gst-registered", null, "GST-REGISTERED", null)]
    [InlineData(EntityIdentifierType.OtherTaxRegistration, "x-99", "jp", "X-99", "JP")]
    public void Valid_identifiers_normalize_and_resolve_their_jurisdiction(
        EntityIdentifierType type, string input, string? jurisdiction, string expectedValue, string? expectedJurisdiction)
    {
        var result = TaxIdentifierFormats.Normalize(type, input, jurisdiction);

        Assert.Equal(expectedValue, result.Value);
        Assert.Equal(expectedJurisdiction, result.Jurisdiction);
    }

    [Theory]
    [InlineData(EntityIdentifierType.EuVat, "DE12345678", null, "Value")] // DE needs 9 digits
    [InlineData(EntityIdentifierType.EuVat, "GR123456789", null, "Value")] // Greece is EL
    [InlineData(EntityIdentifierType.EuVat, "US123456789", null, "Value")] // not a Member State
    [InlineData(EntityIdentifierType.EuVat, "ATX12345678", null, "Value")] // AT needs U + 8 digits
    [InlineData(EntityIdentifierType.EuVat, "SE123456789002", null, "Value")] // SE ends in 01
    [InlineData(EntityIdentifierType.EuVat, "DE123456789", "FR", "Jurisdiction")] // prefix and jurisdiction disagree
    [InlineData(EntityIdentifierType.GbVrn, "GB12345678", null, "Value")]
    [InlineData(EntityIdentifierType.GbVrn, "123456789", "IE", "Jurisdiction")]
    [InlineData(EntityIdentifierType.NzGst, "1234567", null, "Value")]
    [InlineData(EntityIdentifierType.CaBn, "12345678RT0001", null, "Value")]
    [InlineData(EntityIdentifierType.CaBn, "123456789", "US-CA", "Jurisdiction")]
    [InlineData(EntityIdentifierType.ChUid, "CH123456789", null, "Value")]
    [InlineData(EntityIdentifierType.ChUid, "CHE12345678", null, "Value")]
    [InlineData(EntityIdentifierType.UsStateSalesTaxId, "12345", null, "Jurisdiction")] // state required
    [InlineData(EntityIdentifierType.UsStateSalesTaxId, "12345", "US-ZZ", "Jurisdiction")] // not a state
    [InlineData(EntityIdentifierType.UsStateSalesTaxId, "12345", "CA-ON", "Jurisdiction")] // not the US
    [InlineData(EntityIdentifierType.UsStateSalesTaxId, "12$45", "US-NY", "Value")]
    [InlineData(EntityIdentifierType.Abn, "1234567890", null, "Value")]
    [InlineData(EntityIdentifierType.Acn, "12345678", null, "Value")]
    [InlineData(EntityIdentifierType.Gst, "x", null, "Value")]
    [InlineData(EntityIdentifierType.OtherTaxRegistration, "x-99", "Japan", "Jurisdiction")]
    public void Invalid_identifiers_are_rejected_naming_the_field(
        EntityIdentifierType type, string input, string? jurisdiction, string expectedField)
    {
        var ex = Assert.Throws<DomainRuleException>(() => TaxIdentifierFormats.Normalize(type, input, jurisdiction));

        Assert.Equal(expectedField, ex.Field);
    }

    [Fact]
    public void Unknown_scheme_is_rejected()
    {
        var ex = Assert.Throws<DomainRuleException>(() => TaxIdentifierFormats.Normalize((EntityIdentifierType)42, "DE123456789", null));
        Assert.Equal("Type", ex.Field);
    }

    [Fact]
    public void Every_eu_member_state_prefix_is_known()
    {
        // The 27 Member States (Greece as EL) plus XI — a missing prefix would silently reject a real VAT ID.
        string[] prefixes =
        [
            "AT", "BE", "BG", "CY", "CZ", "DE", "DK", "EE", "EL", "ES", "FI", "FR", "HR", "HU", "IE", "IT", "LT",
            "LU", "LV", "MT", "NL", "PL", "PT", "RO", "SE", "SI", "SK", "XI",
        ];
        foreach (var prefix in prefixes)
        {
            // A known prefix reaches its national-shape check; an unknown one would fail on the prefix instead.
            var ex = Assert.Throws<DomainRuleException>(() => TaxIdentifierFormats.Normalize(EntityIdentifierType.EuVat, prefix + "!!", null));
            Assert.Contains($"does not match the {prefix} format", ex.Message, StringComparison.Ordinal);
        }

        // Negative control: an unknown prefix fails on the prefix itself.
        var unknown = Assert.Throws<DomainRuleException>(() => TaxIdentifierFormats.Normalize(EntityIdentifierType.EuVat, "ZZ!!", null));
        Assert.Contains("is not an EU VAT Member State prefix", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entity_stores_the_normalized_identifier_with_its_jurisdiction()
    {
        var entity = EntityRecord.Create(Guid.NewGuid(), EntityType.Company, "Seller GmbH", null, DateTimeOffset.UtcNow, []);

        var vat = entity.AddIdentifier(EntityIdentifierType.EuVat, "de 123456789", DateTimeOffset.UtcNow);

        Assert.Equal("DE123456789", vat.Value);
        Assert.Equal("DE", vat.Jurisdiction);
        // Same number in another spelling is a duplicate (normalized before the check).
        var dup = Assert.Throws<DomainRuleException>(() => entity.AddIdentifier(EntityIdentifierType.EuVat, "DE-123-456-789", DateTimeOffset.UtcNow));
        Assert.Equal("Value", dup.Field);
    }

    [Theory]
    [InlineData(EntityIdentifierType.Abn, "51824753556", "AU")]
    [InlineData(EntityIdentifierType.EuVat, "EL123456789", "GR")]
    [InlineData(EntityIdentifierType.Gst, "GST-1", null)]
    public void Identifiers_stored_before_p2_derive_their_jurisdiction(EntityIdentifierType type, string value, string? expected)
    {
        // A pre-P2 row has no Jurisdiction column value; the effective jurisdiction comes from the scheme.
        var legacy = new EntityIdentifier { Id = Guid.NewGuid(), EntityId = Guid.NewGuid(), Type = type, Value = value };

        Assert.Null(legacy.Jurisdiction);
        Assert.Equal(expected, legacy.EffectiveJurisdiction);
    }
}
