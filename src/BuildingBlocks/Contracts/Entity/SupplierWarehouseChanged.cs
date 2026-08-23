namespace ThreeCommerce.BuildingBlocks.Contracts.Entity;

/// <summary>
/// Published by the Entity service (the supplier lifecycle owner) whenever a supplier's current warehouse
/// address changes — set during onboarding, edited directly pre-approval from the supplier portal, or applied
/// from an approved <c>WarehouseAddress</c> change request post-approval. A supplier that backs
/// <c>Warehouse</c>-fulfilment offers has a warehouse; this is where its stock is collected from.
/// Ordering projects it into a local <c>SupplierWarehouseCopy</c> read model (ADR-0008) so a
/// "Collect at warehouse" checkout can record the warehouse address on the order without querying Entity.
/// <see cref="SupplierId"/> is the supplier's Entity id — the same id <c>Offer.SupplierId</c> carries.
/// </summary>
public sealed record SupplierWarehouseChanged(
    Guid TenantId,
    Guid SupplierId,
    string SupplierName,
    string Line1,
    string? Line2,
    string City,
    string? Region,
    string Postcode,
    string CountryCode);
