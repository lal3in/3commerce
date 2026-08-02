namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

/// <summary>
/// Published by the Order aggregate owner (Ordering's status updater) once an order is paid,
/// carrying the per-supplier cost of goods aggregated over the order's lines (phase 1). Consumed by
/// Payments to wire the COGS accrual (Dr expense.cogs[.store-{id}] / Cr liability.supplier_payable).
/// Only published when at least one line carries a non-zero supplier cost.
/// </summary>
public record OrderCostsRecognized(
    Guid OrderId,
    Guid? StorefrontId,
    Guid TenantId,
    string Currency,
    IReadOnlyList<SupplierCostItem> Items);

/// <summary>The gross cost of goods owed to one supplier for an order (minor units, pre-commission).</summary>
public record SupplierCostItem(Guid SupplierEntityId, long CostMinor);
