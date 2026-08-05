namespace ThreeCommerce.Catalog.Domain;

/// <summary>
/// Local read copy (ADR-0008 / ADR-0042) of a storefront's cross-service go-live signals: whether it has
/// an active carrier (Fulfillment) and an active payment account (Payments). Kept current by consuming
/// StorefrontCarrierReadinessChanged / StorefrontPaymentReadinessChanged. The storefront activation gate
/// reads it so a store can't go live without the ability to charge (and to ship, when it lists physical
/// products). Absent row = neither signal seen yet = not ready.
/// </summary>
public sealed class StorefrontServiceReadiness
{
    public Guid StorefrontId { get; init; }
    public Guid TenantId { get; set; }
    public bool HasActiveCarrier { get; set; }
    public bool HasActivePaymentAccount { get; set; }
}
