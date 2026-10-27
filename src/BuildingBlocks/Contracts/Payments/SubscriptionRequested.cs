using ThreeCommerce.BuildingBlocks.Contracts.Supply;

namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>
/// A confirmed order has a recurring (subscription) line (Phase 7 / mt7_3). Payments sets up a
/// subscription so future periods can be charged behind IPaymentProvider. The first period is paid
/// as part of the order at checkout; this arranges the recurring renewal.
/// <para>
/// The tax TERMS the line was sold under are snapshotted here (ADR-0063 §7.1 / P4): <see cref="TaxInclusive"/>
/// is the storefront's regime when the order was placed (does <see cref="PriceMinor"/> contain the tax?), and
/// <see cref="TaxExempt"/> is whether the line was destination-exempt for the order's ship-to (ProductShipRule,
/// exactly as checkout excluded it from the tax base). The RATE is not carried: a renewal uses the rate in force
/// at the charge. Both appended optional → back-compatible (null = regime unknown, Payments uses its projection).
/// </para>
/// <para>
/// <see cref="PriceMinor"/> is the price of each renewal for the WHOLE line: unit × <see cref="Quantity"/> less the
/// line's renewal discount (owner decision 2026-10-27, ADR-0057 amendment) — the cart's "renews at" figure. Before
/// that decision it was one unit's price whatever the quantity. <see cref="Quantity"/> (appended, default 1) is the
/// number of units the subscription covers; it is informational — the price already includes it.
/// </para>
/// </summary>
public record SubscriptionRequested(
    Guid TenantId, Guid OrderId, string CustomerEmail, Guid ProductId, Guid? VariantId,
    BillingPeriod BillingPeriod, long PriceMinor, string Currency, Guid? StorefrontId = null,
    bool? TaxInclusive = null, bool TaxExempt = false, int Quantity = 1);
