using ThreeCommerce.BuildingBlocks.Contracts.Tax;

namespace ThreeCommerce.BuildingBlocks.Contracts.Entity;

/// <summary>
/// Published by the Entity service whenever the legal identity of a tenant's seller of record changes
/// (ADR-0063 § 7.3, P2): it was designated seller, or its names, tax identifiers, registered office or
/// seller establishments changed. A FULL snapshot, not a delta — a consumer upserts its copy keyed by
/// (<see cref="TenantId"/>, <see cref="EntityId"/>) and ignores a snapshot older than the one it holds
/// (<see cref="OccurredAt"/>), so redelivery and reordering are harmless. Invoicing (phase 1) projects it to
/// print the seller block and resolve the jurisdiction profile. Arrays are concrete so MassTransit's
/// serializer materializes them. Additive only: new members get defaults.
/// </summary>
public sealed record LegalEntityChanged(
    Guid TenantId,
    Guid EntityId,
    string LegalName,
    string? TradingName,
    bool IsSeller,
    LegalEntityTaxIdentifier[] TaxIdentifiers,
    LegalEntityEstablishment[] Establishments,
    LegalEntityAddress? RegisteredOffice,
    DateTimeOffset OccurredAt);

/// <summary>
/// One tax identifier. <see cref="Jurisdiction"/> is an ISO country with an optional subdivision
/// (<c>DE</c>, <c>US-CA</c>, <c>GB-NIR</c>), or null when the scheme does not imply one.
/// </summary>
public sealed record LegalEntityTaxIdentifier(
    Guid Id,
    TaxIdScheme Scheme,
    string Value,
    string? Jurisdiction,
    bool Verified);

/// <summary>A seller establishment; storefronts bind to it by <see cref="Key"/> (Catalog SellerEstablishmentKey).</summary>
public sealed record LegalEntityEstablishment(
    Guid Id,
    string Key,
    string Label,
    string Jurisdiction,
    LegalEntityAddress Address);

public sealed record LegalEntityAddress(
    string Line1,
    string? Line2,
    string City,
    string? Region,
    string Postcode,
    string CountryCode);
