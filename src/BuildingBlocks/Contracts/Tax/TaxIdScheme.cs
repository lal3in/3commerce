namespace ThreeCommerce.BuildingBlocks.Contracts.Tax;

/// <summary>
/// The shared tax-identifier scheme vocabulary (ADR-0063 § 7.3, P2): which register a seller's or buyer's
/// tax ID belongs to. Numeric values equal the Entity service's <c>EntityIdentifierType</c> members of the
/// same name (pinned by a unit test), so a value read from Entity's HTTP API or from
/// <see cref="Entity.LegalEntityChanged"/> means the same thing everywhere. Append only — never renumber.
/// Vocabulary only: each service validates formats itself (no shared domain logic in BuildingBlocks).
/// </summary>
public enum TaxIdScheme
{
    Abn = 1,
    Acn = 2,
    Gst = 3,
    OtherTaxRegistration = 4,
    EuVat = 5,
    GbVrn = 6,
    NzGst = 7,
    CaBn = 8,
    ChUid = 9,
    UsStateSalesTaxId = 10,
}
