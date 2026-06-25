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
    public DateTimeOffset PeriodStart { get; init; }
    public DateTimeOffset? PeriodEnd { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public long RemainingQuantity => Math.Max(0, IncludedQuantity - UsedQuantity);
    public long OverageQuantity => Math.Max(0, UsedQuantity - IncludedQuantity);

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

    /// <summary>Set the plan's included allowance (mt7_4).</summary>
    public void Provision(long includedQuantity, DateTimeOffset? periodEnd, DateTimeOffset now)
    {
        if (includedQuantity < 0)
        {
            throw new FulfillmentRuleException("Included quantity cannot be negative.");
        }

        IncludedQuantity = includedQuantity;
        PeriodEnd = periodEnd;
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
