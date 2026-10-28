namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// A subscription renewal was declined (or cancelled at the provider) and will be retried (dunning, owner decision
/// 2026-10-27, ADR-0057 amendment). Nothing was charged; the subscription is past due. Notifications emails the customer
/// when the next retry is (<see cref="NextRetryAt"/>), when an unpaid period cancels the subscription
/// (<see cref="CancelsAfter"/>) and links the storefront "complete payment" page.
/// </summary>
public record SubscriptionPaymentFailed(
    Guid TenantId, Guid SubscriptionId, Guid? StorefrontId, string CustomerEmail,
    long AmountMinor, string Currency, DateTimeOffset PeriodEnd, DateTimeOffset? NextRetryAt, DateTimeOffset? CancelsAfter);
