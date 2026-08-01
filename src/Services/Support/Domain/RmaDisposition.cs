namespace ThreeCommerce.Support.Domain;

/// <summary>What the operator did with the physically-returned goods, decided AFTER the return is
/// received (independent of the refund, which proceeds on receipt).</summary>
public enum RmaDispositionKind
{
    /// <summary>Returned to sellable inventory (publishes RestockRequested to Fulfillment).</summary>
    Restock = 1,

    /// <summary>Held in storage, NOT returned to sellable stock — carries a reason + comments.</summary>
    Storage = 2,
}

/// <summary>Why returned goods went to storage instead of back on sale.</summary>
public enum RmaStorageReason
{
    Damage = 1,
    Incomplete = 2,
    UnfitForSale = 3,
}

/// <summary>
/// The operator's disposition of a received RMA return (mt4_8+): restock or storage. One per RMA,
/// keyed by the RMA/saga correlation id. Viewable and editable — a mistaken reason or note can be
/// corrected. Storage is a Support-side record (no sellable-stock movement); restock idempotently
/// publishes RestockRequested (idempotent by RMA id downstream).
/// </summary>
public class RmaDisposition
{
    public Guid RmaId { get; init; }
    public RmaDispositionKind Kind { get; set; }
    public RmaStorageReason? StorageReason { get; set; }
    public string? Comments { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
