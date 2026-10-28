namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// The final scheduled retry of an unpaid renewal (day 7 after the first failure) did not settle, so the subscription
/// was cancelled (owner decision 2026-10-27, ADR-0057 amendment). Nothing was charged for the period. Notifications
/// emails the customer.
/// </summary>
public record SubscriptionCancelledForNonPayment(
    Guid TenantId, Guid SubscriptionId, Guid? StorefrontId, string CustomerEmail,
    long AmountMinor, string Currency, DateTimeOffset PeriodEnd);
