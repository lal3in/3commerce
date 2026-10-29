namespace ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;

// The published tax-fact vocabulary (ADR-0063 § 6.2 / § 7.1). Numeric values equal Ordering's per-line tax facts of the
// same names (ThreeCommerce.Ordering.Domain.TaxCategory / TaxExemptionReason / TaxScheme, P1) — pinned by a unit test
// (TaxFactsContractParityTests) — so a fact means the same thing whichever service published it. Enums cross the wire as
// NUMBERS: append members, never renumber. Vocabulary only, no logic (BuildingBlocks carries contracts and plumbing).
// A namespace of its own, deliberately: Ordering imports Contracts.Tax (TaxIdScheme) next to its own domain enums of
// these names, and one namespace for both would make every such reference ambiguous.

/// <summary>The tax category of one charged line (EN 16931 / UNCL5305: <see cref="Standard"/> = S, <see cref="Exempt"/> = E).</summary>
public enum TaxCategory
{
    /// <summary>No tax facts are known for the line (a rate that was never projected, or a row written before the facts existed).</summary>
    NotRecorded = 0,

    /// <summary>Taxed at <see cref="SettledChargeLine.TaxRateBps"/> (which may be 0 % on a storefront that charges no tax).</summary>
    Standard = 1,

    /// <summary>Not taxed — <see cref="SettledChargeLine.ExemptionReason"/> says why.</summary>
    Exempt = 2,
}

/// <summary>Why a line carries no tax although its storefront charges a rate.</summary>
public enum TaxExemptionReason
{
    None = 0,

    /// <summary>The product's ship rule for the ship-to country says the destination charges no tax (ADR-0050).</summary>
    DestinationShipRule = 1,

    /// <summary>Shipping follows the goods' taxability (no goods line of the order is destination-taxable).</summary>
    ShippingFollowsExemptGoods = 2,

    /// <summary>The product's own exempt tax mode.</summary>
    ProductTaxMode = 3,
}

/// <summary>The tax scheme a sale or charge falls under.</summary>
public enum TaxScheme
{
    /// <summary>No tax facts were recorded.</summary>
    NotRecorded = 0,
    Domestic = 1,
    Oss = 2,
    Ioss = 3,
    ReverseCharge = 4,
    IntraEuExempt = 5,
    Export = 6,
    OutOfScope = 7,
}

/// <summary>
/// One line of a settled charge with its tax facts — the P1 shape (ADR-0063 § 7.1 <c>LineTax</c>) a tax document is
/// built from. Amounts are minor units of the charge's currency.
/// </summary>
/// <param name="Description">What was charged, for a document line ("Subscription renewal …", "Usage overage — Api × 120").</param>
/// <param name="Quantity">Units the line covers (subscription units, or metered units).</param>
/// <param name="AmountMinor">
/// The line's SHELF amount — the price as the storefront lists it (ADR-0038): it contains the tax on an inclusive store
/// and excludes it on an exclusive one.
/// </param>
/// <param name="TaxCategory">The line's category.</param>
/// <param name="TaxRateBps">The rate applied, in basis points (0 for an exempt line).</param>
/// <param name="TaxableBaseMinor">
/// The base, always NET of the tax (P1's convention): <c>TaxableBaseMinor + TaxMinor</c> is what the customer paid for
/// the line.
/// </param>
/// <param name="TaxMinor">The tax of the line.</param>
/// <param name="ExemptionReason">Why the line is exempt, or <see cref="TaxExemptionReason.None"/>.</param>
public record SettledChargeLine(
    string Description,
    long Quantity,
    long AmountMinor,
    TaxCategory TaxCategory,
    int TaxRateBps,
    long TaxableBaseMinor,
    long TaxMinor,
    TaxExemptionReason ExemptionReason = TaxExemptionReason.None);
