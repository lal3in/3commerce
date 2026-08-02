namespace ThreeCommerce.BuildingBlocks.Contracts.Support;

/// <summary>
/// Published by Support whenever an operator records or edits the disposition of a received RMA return
/// (phase 1). Support knows the whole-order RMA (an <see cref="OrderId"/> and the refunded gross
/// <see cref="RefundedMinor"/>) but NOT the per-line supplier costs, so it hands off to Ordering — the
/// owner of cost knowledge — which values the returned goods and republishes <c>ReturnedGoodsValued</c>
/// for Payments to correct the COGS accrual. Enums are numeric on the wire (AGENTS.md invariant):
/// <see cref="Kind"/> is <c>RmaDispositionKind</c> (1 = Restock, 2 = Storage) and
/// <see cref="StorageReason"/> is <c>RmaStorageReason</c> (1 = Damage, 2 = Incomplete, 3 = UnfitForSale).
/// <para>
/// <see cref="Revision"/> starts at 1 on the first disposition and increments on every edit, so an
/// edit that flips Restock↔Storage carries a fresh revision. Downstream idempotency is keyed by
/// <c>{RmaId}:{Revision}</c>, and a revision &gt; 1 makes the Payments side reverse the previous
/// revision's posting before applying the new one — keeping the books consistent with the current
/// disposition without ever editing an append-only entry.
/// </para>
/// </summary>
public record RmaDispositionSet(
    Guid RmaId,
    Guid OrderId,
    int Kind,
    int? StorageReason,
    int Revision,
    long RefundedMinor);
