namespace ThreeCommerce.Payments.Domain;

/// <summary>What started a renewal charge attempt (owner decision 2026-10-27, ADR-0057 amendment).</summary>
public enum SubscriptionChargeTrigger
{
    /// <summary>The nightly auto-renew of a due period (the period's first attempt).</summary>
    Renewal = 1,

    /// <summary>A scheduled dunning retry (days 1, 3 and 7 after the first failure).</summary>
    ScheduledRetry = 2,

    /// <summary>The customer on the storefront "complete payment" page.</summary>
    Customer = 3,

    /// <summary>An operator's Renew in admin.</summary>
    Operator = 4,
}

/// <summary>
/// One provider charge of a subscription period: the attempt's own idempotency key and the PaymentIntent it created.
/// A renewal has no <see cref="Payment"/> row, so this is how a provider event about the intent (a customer finishing
/// 3-D Secure → <c>payment_intent.succeeded</c>) finds the period it pays for and settles it exactly once.
/// </summary>
public sealed class SubscriptionChargeAttempt
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid SubscriptionId { get; init; }

    /// <summary>The period this attempt pays for (the subscription's <c>CurrentPeriodEnd</c> when it was made).</summary>
    public DateTimeOffset PeriodEnd { get; init; }

    /// <summary>0 = the period's first attempt; n = its n-th retry (key suffix <c>-a{n}</c>).</summary>
    public int Attempt { get; init; }
    public required string IdempotencyKey { get; init; }
    public SubscriptionChargeTrigger Trigger { get; init; }

    /// <summary>The pinned charge (price + any added tax) — the same for every attempt of the period.</summary>
    public long AmountMinor { get; init; }
    public required string Currency { get; init; }

    /// <summary>The provider's intent id; null when the provider call threw before answering.</summary>
    public string? PaymentIntentId { get; set; }

    /// <summary>The provider's answer (<see cref="PaymentOutcome"/> name), or <see cref="SubscriptionChargeOutcomes.Pending"/> until it answers.</summary>
    public required string Outcome { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the period was settled by THIS attempt (immediately, or later by the provider's success event).</summary>
    public DateTimeOffset? SettledAt { get; set; }
}

/// <summary>Outcome names an attempt carries besides the provider's <see cref="PaymentOutcome"/> names.</summary>
public static class SubscriptionChargeOutcomes
{
    /// <summary>Committed before the provider call and not answered yet (or the call threw): the next attempt reuses its key.</summary>
    public const string Pending = "Pending";

    /// <summary>A past-due reason: the charge could not be made at all (provider unreachable) — no customer-visible failure.</summary>
    public const string Error = "Error";
}
