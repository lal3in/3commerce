using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Usage.Domain;

public sealed class UsageRuleException(string message) : Exception(message);

/// <summary>
/// Whether a balance's billing is held because a charge was not paid (owner decision 2026-10-27,
/// <c>usage_charge_failure_feedback</c>). Crosses HTTP as a number (append only).
/// </summary>
public enum UsagePaymentHold
{
    /// <summary>Every charge is paid or in flight.</summary>
    None = 0,

    /// <summary>A charge was declined and is being retried (days 1, 3 and 7): no further auto-load fires, and usage
    /// beyond the allowance and the prepaid credit needs overage to be allowed.</summary>
    Unpaid = 1,

    /// <summary>A charge's final retry failed and it was abandoned: billable usage (beyond the allowance and the prepaid
    /// credit) is refused until it is paid.</summary>
    Suspended = 2,
}

/// <summary>
/// A customer's metered usage balance for one meter (Phase 7 / mt7_4), owned by the dedicated Usage
/// service. <see cref="UsedQuantity"/> is maintained incrementally as records arrive, so reads are O(1)
/// — the append-only UsageRecords are never re-summed. Provisioned with the plan's included quantity;
/// overage is used beyond included.
/// </summary>
public sealed class UsageBalance
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }

    /// <summary>The storefront this metered balance belongs to (ledger_sf_3): threaded onto overage/auto-load
    /// charges so Payments posts the resulting revenue to the store's own accounts, never a shared default.
    /// Nullable while a balance created by a bare usage record awaits its plan's provisioning.</summary>
    public Guid? StorefrontId { get; private set; }
    public required string CustomerEmail { get; init; }
    public MeterType Meter { get; init; }
    public long IncludedQuantity { get; private set; }
    public long UsedQuantity { get; private set; }
    public bool OverageAllowed { get; private set; }
    public long OverageUnitPriceMinor { get; private set; }
    public string Currency { get; private set; } = "AUD";

    /// <summary>How much overage has already been billed — so re-billing a period doesn't double-charge (mt7_5).</summary>
    public long BilledOverageQuantity { get; private set; }

    // ---- Prepaid auto-load (jobmgr_3): instead of arrears overage, a customer can pre-pay credit that
    // auto-tops-up. When the remaining prepaid credit reaches the customer's threshold, a reload is charged
    // off-session (at OverageUnitPriceMinor) and the credit is added. Disabled by default (classic overage).
    public bool AutoLoadEnabled { get; private set; }

    /// <summary>Top up when prepaid credit falls to/below this many units.</summary>
    public long AutoLoadThresholdQuantity { get; private set; }

    /// <summary>Units added per auto-load (the charge is this × <see cref="OverageUnitPriceMinor"/>).</summary>
    public long AutoLoadReloadQuantity { get; private set; }

    /// <summary>Remaining pre-paid credit units drawn down by usage.</summary>
    public long PrepaidRemainingQuantity { get; private set; }

    /// <summary>Monotonic count of auto-loads applied — makes each top-up's charge reference stable + unique.</summary>
    public long AutoLoadCount { get; private set; }
    public DateTimeOffset PeriodStart { get; private set; }
    public DateTimeOffset? PeriodEnd { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Set from the balance's charges as Payments reports them (usage_charge_failure_feedback): Unpaid while one is being
    /// retried, Suspended once one was abandoned, None again when they are all paid.
    /// </summary>
    public UsagePaymentHold PaymentHold { get; private set; }

    /// <summary>A bounded period is due for close once its end has passed.</summary>
    public bool IsDueForClose(DateTimeOffset now) => PeriodEnd is { } end && end <= now;

    public long RemainingQuantity => Math.Max(0, IncludedQuantity - UsedQuantity);
    public long OverageQuantity => Math.Max(0, UsedQuantity - IncludedQuantity);
    public long UnbilledOverageQuantity => Math.Max(0, OverageQuantity - BilledOverageQuantity);
    public long UnbilledOverageChargeMinor => UnbilledOverageQuantity * OverageUnitPriceMinor;

    /// <summary>Whether a quantity may be consumed: within the allowance or the prepaid credit always; beyond them, overage
    /// allowed or the customer has enabled prepaid auto-load (they've pre-authorized billing, so usage tops up the credit,
    /// mt7_5) — while no charge is held (<see cref="PaymentHold"/>): an unpaid charge stops auto-load granting more
    /// (only allowed overage still accrues, billed in arrears), and an abandoned one suspends billable usage altogether.</summary>
    public bool CanAccept(long quantity)
    {
        if (UsedQuantity + quantity <= IncludedQuantity || (AutoLoadEnabled && PrepaidRemainingQuantity >= quantity))
        {
            return true;
        }

        return PaymentHold switch
        {
            UsagePaymentHold.Suspended => false,
            UsagePaymentHold.Unpaid => OverageAllowed,
            _ => OverageAllowed || AutoLoadEnabled,
        };
    }

    /// <summary>The charge for one auto-load top-up (reload units × the overage unit price).</summary>
    public long AutoLoadChargeMinor => AutoLoadReloadQuantity * OverageUnitPriceMinor;

    /// <summary>Enabled, configured to reload, the prepaid credit has reached the customer's threshold, and no charge is
    /// held — a declined top-up is not followed by another one on the same card (usage_charge_failure_feedback).</summary>
    public bool ShouldAutoLoad() =>
        AutoLoadEnabled && AutoLoadReloadQuantity > 0 && PrepaidRemainingQuantity <= AutoLoadThresholdQuantity
        && PaymentHold == UsagePaymentHold.None;

    private UsageBalance() { }

    public static UsageBalance Create(Guid tenantId, string email, MeterType meter, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CustomerEmail = email.Trim().ToLowerInvariant(),
            Meter = meter,
            PeriodStart = now,
            UpdatedAt = now,
        };

    /// <summary>Set the plan's allowance + overage pricing (mt7_4/mt7_5) and the owning storefront
    /// (ledger_sf_3). A null <paramref name="storefrontId"/> leaves any previously-provisioned attribution
    /// intact, so re-provisioning without a store never clears it.</summary>
    public void Provision(
        long includedQuantity, bool overageAllowed, long overageUnitPriceMinor, string currency, DateTimeOffset? periodEnd, DateTimeOffset now,
        Guid? storefrontId = null)
    {
        if (includedQuantity < 0 || overageUnitPriceMinor < 0)
        {
            throw new UsageRuleException("Included quantity and overage price cannot be negative.");
        }

        IncludedQuantity = includedQuantity;
        OverageAllowed = overageAllowed;
        OverageUnitPriceMinor = overageUnitPriceMinor;
        Currency = string.IsNullOrWhiteSpace(currency) ? Currency : currency.ToUpperInvariant();
        PeriodEnd = periodEnd;
        StorefrontId = storefrontId ?? StorefrontId;
        UpdatedAt = now;
    }

    /// <summary>Mark the current overage as billed so it is not charged again (mt7_5).</summary>
    public void MarkOverageBilled(DateTimeOffset now)
    {
        BilledOverageQuantity = OverageQuantity;
        UpdatedAt = now;
    }

    /// <summary>
    /// Close the current billing period and roll to the next one: usage + billed-overage counters reset to
    /// zero and the window advances by its own length (the append-only UsageRecords keep the full history —
    /// only the rolling per-period counters reset). The caller must bill any unbilled overage BEFORE rolling,
    /// or it is lost. An open-ended balance (no <see cref="PeriodEnd"/>) just resets its counters in place.
    /// </summary>
    public void RollToNextPeriod(DateTimeOffset now)
    {
        if (PeriodEnd is { } end)
        {
            var length = end - PeriodStart;
            PeriodStart = end;
            PeriodEnd = length > TimeSpan.Zero ? end + length : end;
        }
        else
        {
            PeriodStart = now;
        }

        UsedQuantity = 0;
        BilledOverageQuantity = 0;
        UpdatedAt = now;
    }

    /// <summary>Roll a usage record into the balance (incremental — no re-summing on read).</summary>
    public void Add(long quantity, DateTimeOffset now)
    {
        if (quantity <= 0)
        {
            throw new UsageRuleException("Usage quantity must be positive.");
        }

        UsedQuantity += quantity;
        UpdatedAt = now;
    }

    /// <summary>Set the customer's prepaid auto-load preferences (mt7_3 / jobmgr_3). The customer decides the
    /// threshold + reload amount; the unit price is the plan's <see cref="OverageUnitPriceMinor"/>.</summary>
    public void ConfigureAutoLoad(bool enabled, long thresholdQuantity, long reloadQuantity, DateTimeOffset now)
    {
        if (thresholdQuantity < 0 || reloadQuantity < 0)
        {
            throw new UsageRuleException("Auto-load threshold and reload quantity cannot be negative.");
        }

        AutoLoadEnabled = enabled;
        AutoLoadThresholdQuantity = thresholdQuantity;
        AutoLoadReloadQuantity = reloadQuantity;
        UpdatedAt = now;
    }

    /// <summary>Draw usage down against the prepaid credit (never below zero).</summary>
    public void ConsumePrepaid(long quantity, DateTimeOffset now)
    {
        PrepaidRemainingQuantity = Math.Max(0, PrepaidRemainingQuantity - quantity);
        UpdatedAt = now;
    }

    /// <summary>Apply one auto-load: credit the reload units and return the charge to bill off-session.</summary>
    public long ApplyAutoLoad(DateTimeOffset now)
    {
        PrepaidRemainingQuantity += AutoLoadReloadQuantity;
        AutoLoadCount++;
        UpdatedAt = now;
        return AutoLoadChargeMinor;
    }

    /// <summary>
    /// Take back an auto-load credit whose charge was declined (usage_charge_failure_feedback): the credit was granted
    /// optimistically when the top-up was published. Never below zero — units already used from it are not clawed into
    /// a negative balance; the unpaid charge stays owed and is retried.
    /// </summary>
    public void ReverseAutoLoad(long quantity, DateTimeOffset now)
    {
        PrepaidRemainingQuantity = Math.Max(0, PrepaidRemainingQuantity - quantity);
        UpdatedAt = now;
    }

    /// <summary>Re-grant an auto-load credit taken back by <see cref="ReverseAutoLoad"/> once its charge settles after all.</summary>
    public void RestoreAutoLoad(long quantity, DateTimeOffset now)
    {
        PrepaidRemainingQuantity += quantity;
        UpdatedAt = now;
    }

    /// <summary>Hold (or release) billing — derived from the balance's charges by the caller (<see cref="UsageCharge.HoldFor"/>).</summary>
    public void SetPaymentHold(UsagePaymentHold hold, DateTimeOffset now)
    {
        if (PaymentHold != hold)
        {
            PaymentHold = hold;
            UpdatedAt = now;
        }
    }

    /// <summary>
    /// The reference of the overage charge billed now: unique per balance, period and overage level. The overage counters
    /// reset every period, so the level alone repeats — and a repeated reference would be taken for the earlier period's
    /// charge (already posted) and never charged (fixed with usage_charge_failure_feedback).
    /// </summary>
    public string OverageChargeReference => $"overage-{Id}-{PeriodStart.ToUnixTimeSeconds()}-{OverageQuantity}";

    /// <summary>The reference of the auto-load just applied (<see cref="AutoLoadCount"/> is monotonic, never reset).</summary>
    public string AutoLoadChargeReference => $"autoload-{Id}-{AutoLoadCount}";
}

/// <summary>Where a usage charge Usage published stands, as Payments reported it.</summary>
public enum UsageChargeState
{
    /// <summary>Published; credited / marked billed optimistically; no outcome yet.</summary>
    Open = 1,

    /// <summary>Payments settled it.</summary>
    Settled = 2,

    /// <summary>Declined; Payments retries it (days 1, 3, 7). An auto-load's credit was taken back.</summary>
    Unpaid = 3,

    /// <summary>The final retry failed: abandoned. The balance is suspended until it is paid.</summary>
    Abandoned = 4,
}

/// <summary>
/// One overage / auto-load charge Usage asked Payments to collect (usage_charge_failure_feedback, owner decision
/// 2026-10-27), recorded when it is published and moved by Payments' outcome events — the per-reference state that makes
/// a reversal happen once and a late settlement undo it.
/// </summary>
public sealed class UsageCharge
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BalanceId { get; init; }
    public required string Reference { get; init; }
    public UsageChargeKind Kind { get; init; }
    public long Quantity { get; init; }

    /// <summary>The shelf price asked for (before any tax an exclusive store adds).</summary>
    public long AmountMinor { get; init; }
    public required string Currency { get; init; }
    public UsageChargeState State { get; private set; } = UsageChargeState.Open;

    /// <summary>The provider's reason for the last failure, or null.</summary>
    public ChargeFailureReason? LastFailureReason { get; private set; }

    /// <summary>When Payments retries it next (null when none is left).</summary>
    public DateTimeOffset? NextRetryAt { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private UsageCharge() { }

    public static UsageCharge Open(
        Guid tenantId, Guid balanceId, string reference, UsageChargeKind kind, long quantity, long amountMinor, string currency,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            BalanceId = balanceId,
            Reference = reference,
            Kind = kind,
            Quantity = quantity,
            AmountMinor = amountMinor,
            Currency = currency,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>
    /// A failure reported: Unpaid (or Abandoned when final). Returns whether this is the FIRST failure of a charge that was
    /// still open — the one time its optimistic credit is reversed. A settled charge ignores a stale failure.
    /// </summary>
    public bool Fail(ChargeFailureReason reason, DateTimeOffset? nextRetryAt, bool final, DateTimeOffset now)
    {
        if (State == UsageChargeState.Settled)
        {
            return false;
        }

        var wasOpen = State == UsageChargeState.Open;
        State = final ? UsageChargeState.Abandoned : UsageChargeState.Unpaid;
        LastFailureReason = reason;
        NextRetryAt = final ? null : nextRetryAt;
        UpdatedAt = now;
        return wasOpen;
    }

    /// <summary>Settled. Returns whether it had been failed — the one time a reversed credit is re-granted.</summary>
    public bool Settle(DateTimeOffset now)
    {
        if (State == UsageChargeState.Settled)
        {
            return false;
        }

        var wasFailed = State is UsageChargeState.Unpaid or UsageChargeState.Abandoned;
        State = UsageChargeState.Settled;
        LastFailureReason = null;
        NextRetryAt = null;
        UpdatedAt = now;
        return wasFailed;
    }

    /// <summary>The hold a balance's charges put it under: any abandoned → Suspended; any unpaid → Unpaid; else None.</summary>
    public static UsagePaymentHold HoldFor(IEnumerable<UsageChargeState> states)
    {
        var list = states.ToList();
        return list.Contains(UsageChargeState.Abandoned) ? UsagePaymentHold.Suspended
            : list.Contains(UsageChargeState.Unpaid) ? UsagePaymentHold.Unpaid
            : UsagePaymentHold.None;
    }
}

/// <summary>An append-only metered usage event (Phase 7 / mt7_4). Idempotent by ReferenceId.</summary>
public sealed class UsageRecord
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid BalanceId { get; init; }
    public required string CustomerEmail { get; init; }
    public MeterType Meter { get; init; }
    public long Quantity { get; init; }
    public string? ReferenceId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}
