using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>What the customer's own attempt on the "complete payment" page came to.</summary>
public enum SubscriptionPayOutcome
{
    /// <summary>The period is paid; the subscription is active again.</summary>
    Paid = 1,

    /// <summary>The bank wants the customer to authenticate (3-D Secure) — confirm the intent with its client secret.</summary>
    RequiresAction = 2,

    /// <summary>Declined (or cancelled at the provider); still past due, the schedule carries on.</summary>
    Declined = 3,

    /// <summary>The provider could not be reached; nothing was charged, try again.</summary>
    ProviderUnavailable = 4,

    /// <summary>The subscription is not past due — nothing to pay.</summary>
    NotPastDue = 5,

    /// <summary>The chosen saved card is not one of the customer's active cards.</summary>
    UnknownPaymentMethod = 6,
}

/// <summary>The customer's pay attempt: the subscription after it, the outcome, and — for 3-D Secure — the intent to confirm.</summary>
public sealed record SubscriptionPayResult(
    Subscription Subscription, SubscriptionPayOutcome Outcome, string? PaymentIntentId, string? ClientSecret);
