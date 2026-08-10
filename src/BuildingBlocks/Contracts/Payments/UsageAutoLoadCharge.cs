using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// A metered customer's prepaid credit reached their auto-load threshold and a reload should be charged via
/// the rail (jobmgr_3). Prepaid alternative to arrears <see cref="UsageOverageCharge"/>. Idempotent by
/// Reference (one per top-up) so a re-published charge never double-bills.
/// </summary>
public record UsageAutoLoadCharge(
    Guid TenantId, string CustomerEmail, MeterType Meter, long ReloadQuantity, long ChargeMinor, string Currency, string Reference);
