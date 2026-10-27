namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// A subscription renewal charge came back <c>RequiresAction</c> — the customer's bank wants them to authenticate
/// (3DS/SCA) before it will pay (owner decision 2026-10-27, ADR-0057 amendment). The renewal was NOT taken: the
/// subscription is past-due, its period did not advance and nothing was posted. Notifications emails the customer to
/// confirm the payment; a retry of the same period reuses the same idempotency key and pinned amount.
/// <para>
/// <see cref="AmountMinor"/> is what the period's charge asked for (the pinned amount, tax included on an exclusive
/// store); <see cref="PeriodEnd"/> identifies the period (the renewal key is <c>renew-{SubscriptionId}-{PeriodEnd:O}</c>).
/// </para>
/// </summary>
public record SubscriptionRenewalActionRequired(
    Guid TenantId, Guid SubscriptionId, Guid? StorefrontId, string CustomerEmail,
    long AmountMinor, string Currency, DateTimeOffset PeriodEnd);
