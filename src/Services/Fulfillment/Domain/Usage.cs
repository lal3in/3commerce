using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.Fulfillment.Domain;

/// <summary>
/// A customer's metered usage balance for one meter (Phase 7 / mt7_4). <see cref="UsedQuantity"/> is
/// maintained incrementally as records arrive, so reads are O(1) — the append-only UsageRecords are
/// never re-summed. Provisioned with the plan's included quantity; overage is used beyond included.
/// </summary>
public sealed class UsageBalance
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public required string CustomerEmail { get; init; }
    public MeterType Meter { get; init; }
    public long IncludedQuantity { get; private set; }
    public long UsedQuantity { get; private set; }
    public bool OverageAllowed { get; private set; }
    public long OverageUnitPriceMinor { get; private set; }
    public string Currency { get; private set; } = "AUD";

    /// <summary>How much overage has already been billed — so re-billing a period doesn't double-charge (mt7_5).</summary>
    public long BilledOverageQuantity { get; private set; }
    public DateTimeOffset PeriodStart { get; init; }
    public DateTimeOffset? PeriodEnd { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public long RemainingQuantity => Math.Max(0, IncludedQuantity - UsedQuantity);
    public long OverageQuantity => Math.Max(0, UsedQuantity - IncludedQuantity);
    public long UnbilledOverageQuantity => Math.Max(0, OverageQuantity - BilledOverageQuantity);
    public long UnbilledOverageChargeMinor => UnbilledOverageQuantity * OverageUnitPriceMinor;

    /// <summary>Whether a quantity may be consumed: overage allowed, or it stays within the allowance (mt7_5).</summary>
    public bool CanAccept(long quantity) => OverageAllowed || UsedQuantity + quantity <= IncludedQuantity;

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

    /// <summary>Set the plan's allowance + overage pricing (mt7_4/mt7_5).</summary>
    public void Provision(
        long includedQuantity, bool overageAllowed, long overageUnitPriceMinor, string currency, DateTimeOffset? periodEnd, DateTimeOffset now)
    {
        if (includedQuantity < 0 || overageUnitPriceMinor < 0)
        {
            throw new FulfillmentRuleException("Included quantity and overage price cannot be negative.");
        }

        IncludedQuantity = includedQuantity;
        OverageAllowed = overageAllowed;
        OverageUnitPriceMinor = overageUnitPriceMinor;
        Currency = string.IsNullOrWhiteSpace(currency) ? Currency : currency.ToUpperInvariant();
        PeriodEnd = periodEnd;
        UpdatedAt = now;
    }

    /// <summary>Mark the current overage as billed so it is not charged again (mt7_5).</summary>
    public void MarkOverageBilled(DateTimeOffset now)
    {
        BilledOverageQuantity = OverageQuantity;
        UpdatedAt = now;
    }

    /// <summary>Roll a usage record into the balance (incremental — no re-summing on read).</summary>
    public void Add(long quantity, DateTimeOffset now)
    {
        if (quantity <= 0)
        {
            throw new FulfillmentRuleException("Usage quantity must be positive.");
        }

        UsedQuantity += quantity;
        UpdatedAt = now;
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
