namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// Local read copy (ADR-0008 / ADR-0042) of a storefront's cross-service go-live signals: whether it has
/// an active carrier (Fulfillment) and an active payment account (Payments). The storefront activation gate
/// reads it so a store can't go live without the ability to charge (and to ship, when it lists physical
/// products). Absent row = neither signal seen yet = not ready.
/// <para>
/// Read-only: this is a view over <see cref="StorefrontCarrierReadiness"/> and
/// <see cref="StorefrontPaymentReadiness"/>, one row per signal, each written by exactly one consumer.
/// The two signals for a new storefront usually arrive together; when both consumers wrote one shared row,
/// the second first-insert failed (23505), and under the outbox's REPEATABLE READ transaction an upsert or
/// update of a shared row fails the same way (40001). Separate rows never contend.
/// </para>
/// </summary>
public sealed class StorefrontServiceReadiness
{
    public Guid StorefrontId { get; init; }
    public Guid TenantId { get; init; }
    public bool HasActiveCarrier { get; init; }
    public bool HasActivePaymentAccount { get; init; }
}

/// <summary>
/// Carrier half of <see cref="StorefrontServiceReadiness"/>, written only by the consumer of
/// StorefrontCarrierReadinessChanged.
/// </summary>
public sealed class StorefrontCarrierReadiness
{
    public Guid StorefrontId { get; init; }
    public Guid TenantId { get; set; }
    public bool HasActiveCarrier { get; set; }
}

/// <summary>
/// Payment half of <see cref="StorefrontServiceReadiness"/>, written only by the consumer of
/// StorefrontPaymentReadinessChanged.
/// </summary>
public sealed class StorefrontPaymentReadiness
{
    public Guid StorefrontId { get; init; }
    public Guid TenantId { get; set; }
    public bool HasActivePaymentAccount { get; set; }
}
