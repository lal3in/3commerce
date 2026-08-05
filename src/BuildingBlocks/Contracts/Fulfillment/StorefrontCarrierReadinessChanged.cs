namespace ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;

/// <summary>
/// Published when a storefront's carrier availability changes (any carrier configure/lifecycle change,
/// ADR-0042). Catalog keeps a local copy so the storefront go-live gate can require an active carrier
/// when the store lists physical products, without a cross-service query (ADR-0008). Idempotent — the
/// event carries the current truth, so a redelivery just re-asserts it.
/// </summary>
public record StorefrontCarrierReadinessChanged(
    Guid TenantId,
    Guid StorefrontId,
    bool HasActiveCarrier);
