using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Payments.Domain;

public enum SubscriptionStatus { Trialing = 1, Active = 2, PastDue = 3, Cancelled = 4 }

/// <summary>
/// A recurring billing arrangement for a subscription line (Phase 7 / mt7_3). The first period is
/// paid with the order at checkout; renewals charge <see cref="PriceMinor"/> via IPaymentProvider and
/// advance the period ONLY when the charge settles — an unsettled one (failed, needs authentication) duns to
/// <see cref="SubscriptionStatus.PastDue"/> and the period stays put (ADR-0057 amendment 2026-10-27). Lives in
/// Payments (the rail/ledger owner) for now.
/// </summary>
public sealed class Subscription
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }

    /// <summary>The storefront this subscription belongs to (copied from the order at setup). Drives the
    /// per-storefront auto-renew schedule. Null for legacy subscriptions created before storefront threading.</summary>
    public Guid? StorefrontId { get; init; }
    public Guid OrderId { get; init; }
    public required string CustomerEmail { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VariantId { get; init; }
    public BillingPeriod BillingPeriod { get; init; }

    /// <summary>
    /// What each renewal charges (before any tax an exclusive store adds): the WHOLE line — unit × <see cref="Quantity"/>
    /// less the line's renewal discount, computed by Ordering at purchase (owner decision 2026-10-27, ADR-0057).
    /// </summary>
    public long PriceMinor { get; private set; }

    /// <summary>
    /// Units the subscription covers (informational: <see cref="PriceMinor"/> already includes them). 1 for a
    /// subscription set up before quantity was carried — whose price was one unit's, and keeps renewing at that.
    /// </summary>
    public int Quantity { get; private set; } = 1;
    public required string Currency { get; init; }

    /// <summary>
    /// The tax regime <see cref="PriceMinor"/> was sold under (ADR-0063 §7.1 / P4): true = the price CONTAINS
    /// the tax (AU GST / EU VAT), false = tax is added on top (US). Snapshotted at purchase because it fixes what
    /// the price MEANS; null for a subscription set up before the snapshot (the storefront's projection is used).
    /// </summary>
    public bool? TaxInclusive { get; init; }

    /// <summary>The line was destination-exempt at purchase (ProductShipRule for the order's ship-to): renewals post no tax.</summary>
    public bool TaxExempt { get; init; }
    public SubscriptionStatus Status { get; private set; } = SubscriptionStatus.Active;
    public DateTimeOffset CurrentPeriodStart { get; private set; }
    public DateTimeOffset CurrentPeriodEnd { get; private set; }

    /// <summary>
    /// The stored instrument to charge for renewals off-session (a saved card or a direct-debit mandate's
    /// payment method), copied from the order's <see cref="Payment"/> at setup. When present, renewals charge
    /// merchant-initiated (off-session) so they don't re-trigger SCA; when absent, the renewal falls back to
    /// the host's default account (legacy behaviour).
    /// </summary>
    public string? ProviderCustomerId { get; private set; }
    public string? ProviderPaymentMethodId { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Dunning (owner decision 2026-10-27, ADR-0057 amendment): when the CURRENT period's first charge did not settle.
    /// Scheduled retries fall on <see cref="RetryDays"/> after it; null while the subscription is in good standing.
    /// </summary>
    public DateTimeOffset? DunningStartedAt { get; private set; }

    /// <summary>
    /// When the provider last ANSWERED an attempt of the current period (any trigger: nightly, scheduled retry, customer,
    /// operator). An attempt the provider never answered (an outage) does not count, so it never uses up a scheduled retry.
    /// </summary>
    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>
    /// Charge attempts made for the CURRENT period. Attempt 0 uses <see cref="PeriodKey"/>; attempt n uses
    /// <c>{PeriodKey}-a{n}</c> — a FRESH provider idempotency key, because a provider replays a reused key's result
    /// (Stripe: about 24 hours), while the amount stays pinned to <see cref="PeriodKey"/>.
    /// </summary>
    public int AttemptCount { get; private set; }

    /// <summary>Why the current period is past due: <c>RequiresAction</c>, <c>Failed</c>, <c>Cancelled</c> or <c>Error</c>.</summary>
    public string? LastFailureReason { get; private set; }

    /// <summary>Scheduled retries, in days after <see cref="DunningStartedAt"/>; the last one failing cancels the subscription.</summary>
    public static readonly IReadOnlyList<int> RetryDays = [1, 3, 7];

    /// <summary>The period's identity: the renewal key the amount is pinned to (<c>RecurringChargePin</c>).</summary>
    public string PeriodKey => $"renew-{Id}-{CurrentPeriodEnd:O}";

    /// <summary>The provider idempotency key of the NEXT attempt of the current period.</summary>
    public string NextAttemptKey => AttemptCount == 0 ? PeriodKey : $"{PeriodKey}-a{AttemptCount}";

    // Renewal history (mt7_3): one row per period opened, kept on the aggregate so it saves in the same
    // SaveChanges as the state change. Read-only to callers; the aggregate is the sole writer.
    private readonly List<SubscriptionRenewal> _renewals = [];
    public IReadOnlyList<SubscriptionRenewal> Renewals => _renewals;

    public bool IsActive => Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing;

    private Subscription() { }

    public static Subscription Start(
        Guid tenantId, Guid orderId, string email, Guid productId, Guid? variantId,
        BillingPeriod period, long priceMinor, string currency, DateTimeOffset now,
        string? providerCustomerId = null, string? providerPaymentMethodId = null, Guid? storefrontId = null,
        bool? taxInclusive = null, bool taxExempt = false, int quantity = 1)
    {
        if (period == BillingPeriod.Once)
        {
            throw new SubscriptionRuleException("A subscription needs a recurring billing period.");
        }

        if (quantity < 1)
        {
            throw new SubscriptionRuleException("A subscription covers at least one unit.");
        }

        // Every period end derives from this start, and each renewal's idempotency key is built from the period end:
        // hold it at the precision Postgres stores, so the key is the same before and after a reload (StoredTime).
        now = StoredTime.Of(now);
        var subscription = new Subscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            StorefrontId = storefrontId,
            OrderId = orderId,
            CustomerEmail = email.Trim().ToLowerInvariant(),
            ProductId = productId,
            VariantId = variantId,
            BillingPeriod = period,
            PriceMinor = priceMinor,
            Quantity = quantity,
            Currency = currency.ToUpperInvariant(),
            TaxInclusive = taxInclusive,
            TaxExempt = taxExempt,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = Advance(now, period),
            ProviderCustomerId = providerCustomerId,
            ProviderPaymentMethodId = providerPaymentMethodId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        subscription.RecordPeriod(now, priceMinor); // Sequence 1 — the first period, paid with the order at checkout
        return subscription;
    }

    /// <summary>
    /// Advance to the next period after a successful renewal charge. <paramref name="chargedMinor"/> is the gross the
    /// period was charged — <see cref="PriceMinor"/> plus the added tax on an exclusive (US) store (ADR-0057
    /// amendment, owner decision 2026-10-15) — and is recorded on the period's history row; defaults to the price.
    /// </summary>
    public void Renew(DateTimeOffset now, long? chargedMinor = null)
    {
        if (Status == SubscriptionStatus.Cancelled)
        {
            throw new SubscriptionRuleException("Cannot renew a cancelled subscription.");
        }

        CurrentPeriodStart = CurrentPeriodEnd;
        CurrentPeriodEnd = Advance(CurrentPeriodEnd, BillingPeriod);
        Status = SubscriptionStatus.Active;
        UpdatedAt = StoredTime.Of(now);
        DunningStartedAt = null;          // a settled period ends dunning; the new period starts clean
        AttemptCount = 0;
        LastFailureReason = null;
        RecordPeriod(UpdatedAt, chargedMinor ?? PriceMinor); // append the newly-opened period (Sequence n+1)
    }

    /// <summary>
    /// Re-price a subscription set up before quantity was carried (ADR-0057 amendment 2026-10-27,
    /// <c>subscription_quantity_backfill</c>): it was stored as ONE unit at one unit's price less the whole line's
    /// renewal discount; <paramref name="priceMinor"/> is the line's correct renewal price (unit × quantity − the line's
    /// renewal discount). Only a legacy single-unit subscription is corrected — never a downgrade, never a subscription
    /// whose quantity is already carried — so a repeated correction is a no-op. Returns whether anything changed. A
    /// period already attempted keeps its pinned amount (<c>RecurringChargePin</c>); the corrected price applies from the
    /// next period key on.
    /// </summary>
    public bool CorrectLegacyQuantity(int quantity, long priceMinor, DateTimeOffset now)
    {
        if (Quantity != 1 || quantity <= 1 || priceMinor < 0)
        {
            return false;
        }

        Quantity = quantity;
        PriceMinor = priceMinor;
        UpdatedAt = StoredTime.Of(now);
        return true;
    }

    /// <summary>A new attempt key of the current period was issued (<see cref="NextAttemptKey"/> moves on).</summary>
    public void KeyIssued(DateTimeOffset now)
    {
        AttemptCount++;
        UpdatedAt = now;
    }

    /// <summary>
    /// The provider ANSWERED an attempt of the current period (any outcome). Only an answered attempt counts towards the
    /// retry schedule: a provider outage never uses up a scheduled retry.
    /// </summary>
    public void AttemptAnswered(DateTimeOffset now)
    {
        LastAttemptAt = now;
        UpdatedAt = now;
    }

    /// <summary>Dun: the current period did not settle. The first failure of a period starts the retry schedule.</summary>
    public void MarkPastDue(DateTimeOffset now, string reason = "Error")
    {
        if (Status != SubscriptionStatus.Cancelled)
        {
            Status = SubscriptionStatus.PastDue;
            UpdatedAt = StoredTime.Of(now);
            DunningStartedAt ??= UpdatedAt;
            LastFailureReason = reason;
        }
    }

    /// <summary>
    /// The scheduled retry due at <paramref name="now"/>, if any: retry k (1-based) falls due once
    /// <c>DunningStartedAt + RetryDays[k-1]</c> has passed and nothing was attempted since then (a customer's own attempt
    /// on the pay page counts). Several missed days collapse into one retry. <c>IsFinal</c>: the last one — if it does
    /// not settle, the subscription is cancelled.
    /// </summary>
    public (int Retry, bool IsFinal)? ScheduledRetryDue(DateTimeOffset now)
    {
        if (Status != SubscriptionStatus.PastDue || DunningStartedAt is not { } start)
        {
            return null;
        }

        var due = RetryDays.Count(d => start.AddDays(d) <= now);
        if (due == 0 || LastAttemptAt >= start.AddDays(RetryDays[due - 1]))
        {
            return null;
        }

        return (due, due == RetryDays.Count);
    }

    /// <summary>When the next scheduled retry falls due (null when none is left, or not dunning).</summary>
    public DateTimeOffset? NextRetryAt =>
        Status == SubscriptionStatus.PastDue && DunningStartedAt is { } start
            ? RetryDays.Select(d => start.AddDays(d)).Cast<DateTimeOffset?>().FirstOrDefault(t => !(LastAttemptAt >= t))
            : null;

    /// <summary>When an unpaid period's subscription is cancelled: the final scheduled retry.</summary>
    public DateTimeOffset? CancelsAfter => DunningStartedAt?.AddDays(RetryDays[^1]);

    /// <summary>Charge future renewals to another of the customer's stored instruments ("update the card").</summary>
    public void ChangeInstrument(string? providerCustomerId, string providerPaymentMethodId, DateTimeOffset now)
    {
        ProviderCustomerId = providerCustomerId ?? ProviderCustomerId;
        ProviderPaymentMethodId = providerPaymentMethodId;
        UpdatedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = SubscriptionStatus.Cancelled;
        UpdatedAt = StoredTime.Of(now);
    }

    private void RecordPeriod(DateTimeOffset now, long amountMinor) =>
        _renewals.Add(new SubscriptionRenewal
        {
            Id = Guid.CreateVersion7(),
            SubscriptionId = Id,
            Sequence = _renewals.Count + 1,
            PeriodStart = CurrentPeriodStart,
            PeriodEnd = CurrentPeriodEnd,
            AmountMinor = amountMinor,
            Currency = Currency,
            RecordedAt = now,
        });

    private static DateTimeOffset Advance(DateTimeOffset from, BillingPeriod period) => period switch
    {
        BillingPeriod.Monthly => from.AddMonths(1),
        BillingPeriod.Yearly => from.AddYears(1),
        _ => from,
    };
}

public sealed class SubscriptionRuleException(string message) : Exception(message);
