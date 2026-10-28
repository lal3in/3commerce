using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.Entity.Domain;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0063 P3 drift guard (unit lane — no containers). A buyer's tax ID is format-checked by Ordering's own
/// <see cref="BuyerTaxIdFormats"/>, a deliberate COPY of Entity's P2 <see cref="TaxIdentifierFormats"/> (services
/// share contracts, never domain logic — AGENTS.md). This test is the only place both are visible, so it pins
/// that the two copies agree on every scheme: the same inputs are accepted with the same normalized value and
/// jurisdiction, and rejected naming the corresponding field. Change one copy without the other and it fails.
/// </summary>
public class BuyerTaxIdFormatParityTests
{
    // A corpus per scheme: well-formed shapes, near misses and garbage — the edges each rule draws.
    private static readonly string[] Values =
    [
        "DE 123 456 789", "DE12345678", "fr-ab-123456789", "ATU12345678", "ATX12345678", "NL123456789B01", "EL123456789",
        "GR123456789", "IE1234567T", "XI123456789", "SE123456789001", "SE123456789002", "ZZ123", "DE", "",
        "GB 123 4567 89", "123456789", "GBGD001", "GB12345678", "123-456-789", "12345678", "1234567",
        "123456789 RT 0001", "12345678RT0001", "CHE-123.456.789 MWST", "che123456789", "CH123456789", "CHE12345678",
        "sr ab 12-345678", "12$45", "51 824 753 556", "1234567890", "123 456 789", "gst-registered", "x", "x-99",
        new string('9', 81),
    ];

    private static readonly string?[] Jurisdictions = [null, "", "AU", "us-ca", "US-ZZ", "CA-ON", "ca-qc", "IE", "FR", "DE", "GB", "Japan", "jp"];

    [Fact]
    public void Ordering_and_Entity_agree_on_every_scheme_value_and_jurisdiction()
    {
        var accepted = 0;
        var rejected = 0;
        foreach (var scheme in Enum.GetValues<TaxIdScheme>())
        {
            foreach (var value in Values)
            {
                foreach (var jurisdiction in Jurisdictions)
                {
                    var entity = Run(() =>
                    {
                        var r = TaxIdentifierFormats.Normalize((EntityIdentifierType)(int)scheme, value, jurisdiction);
                        return (r.Value, r.Jurisdiction);
                    });
                    var ordering = Run(() =>
                    {
                        var r = BuyerTaxIdFormats.Normalize(scheme, value, jurisdiction);
                        return (r.Value, r.Jurisdiction);
                    });

                    var context = $"{scheme} '{value}' jurisdiction '{jurisdiction ?? "<null>"}'";
                    Assert.True(entity == ordering, $"{context}: Entity {entity} vs Ordering {ordering}");
                    if (entity.Error is null)
                    {
                        accepted++;
                    }
                    else
                    {
                        rejected++;
                    }
                }
            }
        }

        // Negative control: the corpus exercises both sides of every rule, so "agree" is not vacuous.
        Assert.True(accepted > 50, $"only {accepted} accepted combinations");
        Assert.True(rejected > 50, $"only {rejected} rejected combinations");
    }

    [Fact]
    public void An_unknown_scheme_is_rejected_by_both()
    {
        Assert.Equal("Type", Assert.Throws<DomainRuleException>(() =>
            TaxIdentifierFormats.Normalize((EntityIdentifierType)42, "DE123456789", null)).Field);
        Assert.Equal(BuyerIdentity.TaxIdSchemeField, Assert.Throws<BuyerIdentityException>(() =>
            BuyerTaxIdFormats.Normalize((TaxIdScheme)42, "DE123456789", null)).Field);
    }

    // The outcome in one comparable shape: the normalized pair, or the field at fault mapped onto Entity's names.
    private static (string? Value, string? Jurisdiction, string? Error) Run(Func<(string Value, string? Jurisdiction)> normalize)
    {
        try
        {
            var (value, jurisdiction) = normalize();
            return (value, jurisdiction, null);
        }
        catch (DomainRuleException ex)
        {
            return (null, null, ex.Field);
        }
        catch (BuyerIdentityException ex)
        {
            return (null, null, ex.Field switch
            {
                BuyerIdentity.TaxIdField => TaxIdentifierFormats.ValueField,
                BuyerIdentity.TaxIdJurisdictionField => TaxIdentifierFormats.JurisdictionField,
                BuyerIdentity.TaxIdSchemeField => "Type",
                var other => other,
            });
        }
    }
}
