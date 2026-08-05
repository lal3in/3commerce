namespace ThreeCommerce.BuildingBlocks.Contracts.Supply;

/// <summary>
/// The nature of a product (ADR-0028) — what the catalog surfaces and filters on, and what a tenant's
/// shipping policy keys off (which types require a carrier). Distinct from ProductKind (Standard/Bundle)
/// and from the Offer's fulfilment mechanics: a Subscription product is billed recurring and a UsageBased
/// product is metered, but those charging details live on the Offer. Shared in Contracts.Supply (like
/// FulfilmentType/SupplyCategory) so Ordering can resolve a cart line's type for the checkout shipping
/// gate. Int-backed and additive (no migration to extend); legacy rows persisted as 0 render as Physical.
/// </summary>
public enum ProductType
{
    Physical = 1,
    Digital = 2,
    Service = 3,
    Bundle = 4,
    Subscription = 5,
    UsageBased = 6,
}
