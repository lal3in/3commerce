namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// Published when a storefront's payment-account availability changes (any account create/lifecycle
/// change, ADR-0042). Catalog keeps a local copy so the storefront go-live gate can require an active
/// payment account, without a cross-service query (ADR-0008). Idempotent — the event carries the
/// current truth, so a redelivery just re-asserts it.
/// <para>
/// <paramref name="AsOf"/> is when that truth was read. The bus outbox does not keep publish order: two
/// transitions are two outbox batches, and the delivery service sends batches in parallel under load, so a
/// later "active" can reach Catalog before an earlier "not active". Catalog applies an event only if it is
/// at least as new as the copy it holds. Null (a publisher from before this field) means "apply as before".
/// </para>
/// </summary>
public record StorefrontPaymentReadinessChanged(
    Guid TenantId,
    Guid StorefrontId,
    bool HasActivePaymentAccount,
    DateTimeOffset? AsOf = null);
