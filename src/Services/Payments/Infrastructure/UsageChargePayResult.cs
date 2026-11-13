using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Infrastructure;

/// <summary>What the customer's own attempt on the usage "complete payment" page came to (<c>usage_charge_pay_page</c>).
/// Crosses HTTP as a number; the values mirror <see cref="SubscriptionPayOutcome"/>.</summary>
public enum UsageChargePayOutcome
{
    /// <summary>The charge is paid (posted; Usage re-grants a taken-back credit and lifts the hold).</summary>
    Paid = 1,

    /// <summary>The bank wants the customer to authenticate (3-D Secure) — confirm the intent with its client secret.</summary>
    RequiresAction = 2,

    /// <summary>Declined (or cancelled at the provider); still owed, the schedule carries on.</summary>
    Declined = 3,

    /// <summary>The provider could not be reached; nothing was charged, try again.</summary>
    ProviderUnavailable = 4,

    /// <summary>Nothing to pay: the charge is settled, in flight, or was never attempted (a skipped top-up).</summary>
    NotPayable = 5,

    /// <summary>The chosen saved card is not one of the customer's active cards.</summary>
    UnknownPaymentMethod = 6,
}

/// <summary>The customer's pay attempt: the charge after it, the outcome, and — for 3-D Secure — the intent to confirm.</summary>
public sealed record UsageChargePayResult(UsageCharge Charge, UsageChargePayOutcome Outcome, string? PaymentIntentId, string? ClientSecret);
