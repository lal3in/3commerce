using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Usage.Domain;

public sealed class UsageRuleException(string message) : Exception(message);

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

    /// <summary>A bounded period is due for close once its end has passed.</summary>
    public bool IsDueForClose(DateTimeOffset now) => PeriodEnd is { } end && end <= now;

    public long RemainingQuantity => Math.Max(0, IncludedQuantity - UsedQuantity);
    public long OverageQuantity => Math.Max(0, UsedQuantity - IncludedQuantity);
    public long UnbilledOverageQuantity => Math.Max(0, OverageQuantity - BilledOverageQuantity);
    public long UnbilledOverageChargeMinor => UnbilledOverageQuantity * OverageUnitPriceMinor;

    /// <summary>Whether a quantity may be consumed: overage allowed, within the allowance, or the customer
    /// has enabled prepaid auto-load (they've pre-authorized billing, so usage tops up the credit, mt7_5).</summary>
    public bool CanAccept(long quantity) =>
        OverageAllowed || UsedQuantity + quantity <= IncludedQuantity || AutoLoadEnabled;

    /// <summary>The charge for one auto-load top-up (reload units × the overage unit price).</summary>
    public long AutoLoadChargeMinor => AutoLoadReloadQuantity * OverageUnitPriceMinor;

    /// <summary>Enabled, configured to reload, and the prepaid credit has reached the customer's threshold.</summary>
    public bool ShouldAutoLoad() =>
        AutoLoadEnabled && AutoLoadReloadQuantity > 0 && PrepaidRemainingQuantity <= AutoLoadThresholdQuantity;

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
