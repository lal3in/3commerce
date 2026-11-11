using ThreeCommerce.BuildingBlocks.Contracts.Tax;

namespace ThreeCommerce.Identity.Domain;

/// <summary>
/// A customer's saved default business identity (ADR-0063 P3b): the company they usually buy for and, when it has
/// one, its tax registration — normalized per <see cref="TaxIdScheme"/>, with the jurisdiction the number belongs
/// to. It only PREFILLS checkout's "Business purchase" block: the shopper can change it per order, and each order
/// keeps its own snapshot (Ordering's <c>BuyerCompanyName</c>/<c>BuyerTaxId…</c> columns), so editing or clearing
/// the profile never rewrites an order.
/// </summary>
public sealed record BusinessIdentity(string CompanyName, string? TaxId, TaxIdScheme? TaxIdScheme, string? TaxIdJurisdiction);

/// <summary>
/// A business identity the profile refuses. <see cref="Field"/> is one of the <c>BusinessIdentityRules.*Field</c>
/// names, so the endpoint's 400 problem+json can say which input is at fault.
/// </summary>
public sealed class BusinessIdentityException(string message, string field) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// Validates and normalizes a business identity with the SAME rules checkout applies to its business-purchase block
/// (Ordering's <c>BuyerIdentity.Resolve</c>), so a saved identity is always one checkout accepts. The one difference:
/// a profile has no bill-to address, so a US state sales-tax ID must name its state explicitly (checkout can borrow it
/// from a US bill-to region).
/// </summary>
public static class BusinessIdentityRules
{
    public const string CompanyNameField = "CompanyName";
    public const string TaxIdField = "TaxId";
    public const string TaxIdSchemeField = "TaxIdScheme";
    public const string TaxIdJurisdictionField = "TaxIdJurisdiction";

    /// <summary>Bounds of the persisted columns — the same as Ordering's buyer columns, so a saved value always fits an order.</summary>
    public const int CompanyNameMaxLength = 200;
    public const int TaxIdMaxLength = 80;
    public const int TaxIdJurisdictionMaxLength = 10;

    /// <summary>
    /// The company name is required (≤ 200 characters). The tax ID is optional, but a tax ID needs its scheme and a
    /// scheme needs its tax ID. The number is format-checked per scheme by <see cref="BusinessTaxIdFormats"/>.
    /// </summary>
    public static BusinessIdentity Resolve(string? companyName, string? taxId, TaxIdScheme? scheme, string? taxIdJurisdiction)
    {
        var name = companyName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            throw new BusinessIdentityException("Enter the company name.", CompanyNameField);
        }

        if (name.Length > CompanyNameMaxLength)
        {
            throw new BusinessIdentityException($"The company name can be at most {CompanyNameMaxLength} characters.", CompanyNameField);
        }

        if (string.IsNullOrWhiteSpace(taxId))
        {
            if (scheme is not null)
            {
                throw new BusinessIdentityException("Enter the tax ID, or clear the tax-ID scheme.", TaxIdField);
            }

            return new BusinessIdentity(name, null, null, null);
        }

        if (scheme is not { } chosen)
        {
            throw new BusinessIdentityException("Choose which register the tax ID belongs to (its scheme).", TaxIdSchemeField);
        }

        if (taxId.Trim().Length > TaxIdMaxLength)
        {
            throw new BusinessIdentityException($"The tax ID can be at most {TaxIdMaxLength} characters.", TaxIdField);
        }

        var normalized = BusinessTaxIdFormats.Normalize(chosen, taxId, taxIdJurisdiction);
        return new BusinessIdentity(name, normalized.Value, chosen, normalized.Jurisdiction);
    }
}

