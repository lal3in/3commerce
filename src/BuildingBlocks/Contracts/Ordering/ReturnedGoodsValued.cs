namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

/// <summary>
/// Published by Ordering after it values an RMA's returned goods against the order's supplier lines
/// (phase 1), in response to <c>RmaDispositionSet</c>. <see cref="CostMinor"/> is the GROSS supplier
/// cost of the returned goods (unit <c>OfferCopy.SupplierCostMinor</c> × returned quantity/proportion),
/// relabelled into the order's currency with no FX conversion — the same posture as the COGS accrual
/// that produced <c>OrderCostsRecognized</c>. Only published when <see cref="CostMinor"/> &gt; 0.
/// <para>
/// Consumed by Payments, which corrects the order's COGS accrual: a Restock reverses it (a restocked
/// unit re-accrues COGS when resold, so without the reversal it double-expenses); a Storage reclasses
/// it to the write-off expense (total expense unchanged, but the loss surfaces as its own P&amp;L line).
/// <see cref="Kind"/>/<see cref="StorageReason"/> are numeric enums (see <c>RmaDispositionSet</c>);
/// <see cref="Revision"/> is carried through so each disposition edit is idempotency-distinct.
/// </para>
/// </summary>
public record ReturnedGoodsValued(
    Guid RmaId,
    Guid OrderId,
    Guid? StorefrontId,
    Guid TenantId,
    long CostMinor,
    string Currency,
    int Kind,
    int? StorageReason,
    int Revision);
