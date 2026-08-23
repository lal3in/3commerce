namespace ThreeCommerce.BuildingBlocks.Contracts.Ordering;

/// <summary>
/// Published by Ordering (the Order aggregate owner) when a fulfilling supplier — or an admin — marks an
/// order delivered from the supplier portal. The order transitions <c>Confirmed → Delivered</c>. Consumed
/// by Fulfillment to close out the order's shipments (mark them <c>Delivered</c>) and by Notifications for
/// the delivery confirmation. <see cref="SupplierId"/> is the supplier that recorded the delivery (the
/// acting supplier's Entity id), or <see cref="Guid.Empty"/> when an admin recorded it.
/// </summary>
public sealed record OrderDelivered(
    Guid OrderId,
    Guid TenantId,
    Guid SupplierId);
