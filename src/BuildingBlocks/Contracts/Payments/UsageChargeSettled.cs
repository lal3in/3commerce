using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Contracts.Tax.Facts;

namespace ThreeCommerce.BuildingBlocks.Contracts.Payments;

/// <summary>Which usage charge it is (Usage service): arrears overage or a prepaid auto-load top-up. Crosses as a number.</summary>
public enum UsageChargeKind
{
    Overage = 1,
    AutoLoad = 2,
}

/// <summary>
/// A metered usage charge (<see cref="UsageOverageCharge"/> / <see cref="UsageAutoLoadCharge"/>) SETTLED: the provider took
/// the money and the ledger posted its sale under <see cref="Reference"/> (ADR-0063 § 6.2 / P4b). Published through the bus
/// outbox in the SAME unit of work as that journal entry, exactly once per charge — on its first attempt, a dunning retry
/// (owner decision 2026-10-27, <c>usage_charge_failure_feedback</c>) or a later provider success event. Never for an attempt
/// that did not settle (that is <see cref="UsageChargeFailed"/>). Usage confirms the overage / top-up with it; the Invoicing
/// service issues one invoice per event, keyed <c>Usage:{Reference}</c>.
/// </summary>
/// <param name="Quantity">Metered units charged.</param>
/// <param name="UnitPriceMinor">The shelf price per unit (ADR-0038).</param>
/// <param name="Reference">The usage charge reference (Usage's key, and the ledger reference of the sale).</param>
/// <param name="NetMinor">Revenue: <see cref="GrossMinor"/> less <see cref="TaxMinor"/>.</param>
/// <param name="TaxMinor">The tax posted to <c>tax.store-{id}</c>.</param>
/// <param name="GrossMinor">What the customer was charged.</param>
/// <param name="TaxInclusive">Whether the price contains the tax (inclusive) or the tax was added (exclusive).</param>
/// <param name="TaxScheme">Domestic (the storefront's own rate), NotRecorded when the rate was unknown.</param>
/// <param name="Lines">The charged line with its tax facts (one per meter).</param>
/// <param name="SettledAt">When the charge settled.</param>
/// <param name="Attempt">Which attempt settled it (0 = the first; n = the n-th retry).</param>
/// <param name="PaymentIntentId">The provider's intent that settled it.</param>
public record UsageChargeSettled(
    Guid TenantId,
    Guid? StorefrontId,
    string CustomerEmail,
    UsageChargeKind Kind,
    MeterType Meter,
    long Quantity,
    long UnitPriceMinor,
    string Reference,
    string Currency,
    long NetMinor,
    long TaxMinor,
    long GrossMinor,
    bool TaxInclusive,
    TaxScheme TaxScheme,
    IReadOnlyList<SettledChargeLine> Lines,
    DateTimeOffset SettledAt,
    int Attempt = 0,
    string? PaymentIntentId = null);
