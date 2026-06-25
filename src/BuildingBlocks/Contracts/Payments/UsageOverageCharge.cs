using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// A customer's metered usage exceeded its allowance and the overage should be charged via the rail
/// (Phase 7 / mt7_5). Idempotent by Reference so a re-bill of the same overage never double-charges.
/// </summary>
public record UsageOverageCharge(
    Guid TenantId, string CustomerEmail, MeterType Meter, long OverageQuantity, long ChargeMinor, string Currency, string Reference);
