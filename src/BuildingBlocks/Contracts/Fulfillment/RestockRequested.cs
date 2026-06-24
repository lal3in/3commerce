namespace ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;

/// <summary>
/// Manual restock of returned items into inventory (mt4_8). Raised by the RMA return-received flow
/// (operator-driven, partial allowed) or the admin restock endpoint. Fulfillment increments on-hand
/// and records a `Returned` movement; idempotent by ReferenceId.
/// </summary>
public record RestockRequested(Guid TenantId, Guid ReferenceId, IReadOnlyList<RestockItemInfo> Items);

public record RestockItemInfo(Guid ProductId, Guid? VariantId, Guid LocationId, int Quantity);
