using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// A metered customer's prepaid credit reached their auto-load threshold and a reload should be charged via
/// the rail (jobmgr_3). Prepaid alternative to arrears <see cref="UsageOverageCharge"/>. Idempotent by
/// Reference (one per top-up) so a re-published charge never double-bills.
/// <para>
/// Appended (ADR-0063 P4b, 2026-10-28): <c>UnitPriceMinor</c> — the shelf price per unit (ReloadQuantity × it =
/// ChargeMinor), carried onto <see cref="UsageChargeSettled"/> for the invoice line; 0 from an older publisher.
/// </para>
/// </summary>
public record UsageAutoLoadCharge(
    Guid TenantId, string CustomerEmail, MeterType Meter, long ReloadQuantity, long ChargeMinor, string Currency, string Reference,
    Guid? StorefrontId = null, long UnitPriceMinor = 0);
