namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// Published when a storefront's payment-account availability changes (any account create/lifecycle
/// change, ADR-0042). Catalog keeps a local copy so the storefront go-live gate can require an active
/// payment account, without a cross-service query (ADR-0008). Idempotent — the event carries the
/// current truth, so a redelivery just re-asserts it.
/// </summary>
public record StorefrontPaymentReadinessChanged(
    Guid TenantId,
    Guid StorefrontId,
    bool HasActivePaymentAccount);
