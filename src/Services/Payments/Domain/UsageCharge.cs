using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Payments.Domain;

/// <summary>Where a usage charge stands in Payments.</summary>
public enum UsageChargeStatus
{
    /// <summary>Recorded; its first attempt was not answered yet (committed before the provider call).</summary>
    Pending = 1,

    /// <summary>Charged and posted (the <c>Reference</c> journal entry exists) — final.</summary>
    Settled = 2,

    /// <summary>Not settled; dunning — retried on days 1, 3 and 7 after the first failure (<see cref="DunningSchedule"/>).</summary>
    PastDue = 3,

    /// <summary>The final retry did not settle: no more retries. Money arriving later for one of its attempts still settles it.</summary>
    Abandoned = 4,
}

/// <summary>What started a usage charge attempt.</summary>
public enum UsageChargeTrigger
{
    /// <summary>The Usage service's charge request (the charge's first attempt).</summary>
    Initial = 1,

    /// <summary>A scheduled dunning retry (days 1, 3 and 7 after the first failure).</summary>
    ScheduledRetry = 2,
}

/// <summary>
/// One metered usage charge Payments was asked to collect — an arrears overage or a prepaid auto-load top-up
/// (owner decision 2026-10-27, <c>usage_charge_failure_feedback</c>). Before it, a usage charge left no state in Payments
/// beyond its journal entry: a declined charge was logged and forgotten while Usage had already treated it as billed. Now
/// it is dunned exactly like a subscription renewal (<see cref="DunningSchedule"/>): every attempt is a
/// <see cref="UsageChargeAttempt"/> committed before the provider call, an ANSWERED failure is followed by a fresh provider
/// key (<c>{Reference}-a{n}</c>), the amount stays pinned to <see cref="Reference"/> (<c>RecurringChargePin</c>), and the
/// outcome is published back to Usage (<see cref="UsageChargeSettled"/> / <see cref="UsageChargeFailed"/>).
/// </summary>
public sealed class UsageCharge
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid? StorefrontId { get; init; }
    public required string CustomerEmail { get; init; }
    public UsageChargeKind Kind { get; init; }
    public MeterType Meter { get; init; }

    /// <summary>Metered units charged.</summary>
    public long Quantity { get; init; }

    /// <summary>The shelf price per unit (0 when an older Usage did not send it and it does not divide evenly).</summary>
    public long UnitPriceMinor { get; init; }

    /// <summary>The shelf price of the charge (before any tax an exclusive store adds) — what Usage asked for.</summary>
    public long PriceMinor { get; init; }
    public required string Currency { get; init; }

    /// <summary>Usage's charge reference: the de-dupe key, the pinned amount's key, the first attempt's provider key and the ledger reference.</summary>
    public required string Reference { get; init; }
    public required string Description { get; init; }
    public UsageChargeStatus Status { get; private set; } = UsageChargeStatus.Pending;

    /// <summary>When the first attempt failed (dunning start); null while it never failed.</summary>
    public DateTimeOffset? DunningStartedAt { get; private set; }

    /// <summary>When the provider last ANSWERED an attempt (an outage never uses up a scheduled retry).</summary>
    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>Provider keys issued so far; the next attempt uses <see cref="NextAttemptKey"/>.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>Why it is past due: <c>RequiresAction</c>, <c>Failed</c>, <c>Cancelled</c> or <c>Error</c>.</summary>
    public string? LastFailureReason { get; private set; }
    public DateTimeOffset? SettledAt { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The provider idempotency key of the NEXT attempt.</summary>
    public string NextAttemptKey => DunningSchedule.AttemptKey(Reference, AttemptCount);

    private UsageCharge() { }

    public static UsageCharge Record(
        Guid tenantId, Guid? storefrontId, string email, UsageChargeKind kind, MeterType meter, long quantity, long unitPriceMinor,
        long priceMinor, string currency, string reference, string description, DateTimeOffset now)
    {
        now = StoredTime.Of(now);
        return new UsageCharge
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            StorefrontId = storefrontId,
            CustomerEmail = email.Trim().ToLowerInvariant(),
            Kind = kind,
            Meter = meter,
            Quantity = quantity,
            // An older publisher sends no unit price: derive it when it divides evenly, never a rounded guess.
            UnitPriceMinor = unitPriceMinor > 0 ? unitPriceMinor
                : quantity > 0 && priceMinor % quantity == 0 ? priceMinor / quantity : 0,
            PriceMinor = priceMinor,
            Currency = currency.ToUpperInvariant(),
            Reference = reference,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>A new attempt key was issued (<see cref="NextAttemptKey"/> moves on).</summary>
    public void KeyIssued(DateTimeOffset now)
    {
        AttemptCount++;
        UpdatedAt = StoredTime.Of(now);
    }

    /// <summary>The provider ANSWERED an attempt (any outcome) — only this counts towards the retry schedule.</summary>
    public void AttemptAnswered(DateTimeOffset now)
    {
        UpdatedAt = StoredTime.Of(now);
        LastAttemptAt = UpdatedAt;
    }

    /// <summary>Dun: the charge did not settle. The first failure starts the retry schedule. An abandoned charge stays abandoned.</summary>
    public void MarkPastDue(DateTimeOffset now, string reason)
    {
        if (Status is UsageChargeStatus.Settled or UsageChargeStatus.Abandoned)
        {
            return;
        }

        Status = UsageChargeStatus.PastDue;
        UpdatedAt = StoredTime.Of(now);
        DunningStartedAt ??= UpdatedAt;
        LastFailureReason = reason;
    }

    /// <summary>The final retry did not settle: stop retrying.</summary>
    public void Abandon(DateTimeOffset now)
    {
        if (Status == UsageChargeStatus.Settled)
        {
            return;
        }

        Status = UsageChargeStatus.Abandoned;
        UpdatedAt = StoredTime.Of(now);
    }

    /// <summary>Settled (posted). Final: it is never charged again.</summary>
    public void Settle(DateTimeOffset now)
    {
        Status = UsageChargeStatus.Settled;
        UpdatedAt = StoredTime.Of(now);
        SettledAt = UpdatedAt;
        LastFailureReason = null;
    }

    /// <summary>The scheduled retry due at <paramref name="now"/>, if any (<see cref="DunningSchedule.Due"/>); only while past due.</summary>
    public (int Retry, bool IsFinal)? ScheduledRetryDue(DateTimeOffset now) =>
        Status == UsageChargeStatus.PastDue && DunningStartedAt is { } start ? DunningSchedule.Due(start, LastAttemptAt, now) : null;

    /// <summary>When the next scheduled retry falls due (null when none is left, or not dunning).</summary>
    public DateTimeOffset? NextRetryAt =>
        Status == UsageChargeStatus.PastDue && DunningStartedAt is { } start ? DunningSchedule.NextRetryAt(start, LastAttemptAt) : null;

    /// <summary>When an unpaid charge is abandoned: the final scheduled retry.</summary>
    public DateTimeOffset? AbandonsAfter => DunningStartedAt is { } start ? DunningSchedule.EndsAfter(start) : null;
}

/// <summary>
/// One provider charge of a <see cref="UsageCharge"/>: the attempt's own idempotency key and the PaymentIntent it created,
/// so a provider event about the intent (a late <c>payment_intent.succeeded</c>) finds the charge and settles it once.
/// Mirrors <see cref="SubscriptionChargeAttempt"/>.
/// </summary>
public sealed class UsageChargeAttempt
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid UsageChargeId { get; init; }

    /// <summary>0 = the first attempt (key = the reference); n = the n-th retry (key suffix <c>-a{n}</c>).</summary>
    public int Attempt { get; init; }
    public required string IdempotencyKey { get; init; }
    public UsageChargeTrigger Trigger { get; init; }

    /// <summary>The pinned charge (price + any added tax) — the same for every attempt.</summary>
    public long AmountMinor { get; init; }
    public required string Currency { get; init; }

    /// <summary>The provider's intent id; null when the provider call threw before answering.</summary>
    public string? PaymentIntentId { get; set; }

    /// <summary>The provider's answer (<see cref="PaymentOutcome"/> name), or <see cref="SubscriptionChargeOutcomes.Pending"/> until it answers.</summary>
    public required string Outcome { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SettledAt { get; set; }
}
