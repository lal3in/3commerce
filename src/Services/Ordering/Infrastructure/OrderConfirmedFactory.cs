using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure;

/// <summary>
/// Builds the rich <see cref="OrderConfirmed"/> from a confirmed <see cref="Order"/> aggregate — the ONE mapping
/// both the live confirmation path (<see cref="Consumers.OrderStatusConsumer"/>) and the operator backfill
/// (<c>tools/order-confirmed-backfill</c>, ADR-0060) use, so a re-delivered event is the event the order would
/// have produced at confirmation. Pure: everything comes from the aggregate and its lines, in
/// <see cref="Order.Lines"/> order.
/// </summary>
public static class OrderConfirmedFactory
{
    public static OrderConfirmed From(Order order)
    {
        // The refund basis (rma_disc): every consumer that has to value a line — Support's RMA snapshot
        // above all — needs the line's DISCOUNTED worth, not its shelf price. OrderLine.DiscountMinor
        // holds only the promotion share, so the storefront-wide remainder is apportioned here.
        var lineDiscounts = OrderLineDiscounts.For(order);
        return new OrderConfirmed(
            order.Id, order.TenantId, order.Email, order.GrossMinor, order.Currency,
            new ShipToInfo(order.ShipName, order.ShipLine1, order.ShipCity, order.ShipPostcode, order.ShipCountry, order.ShipRegion),
            order.Lines.Select((l, i) => new OrderLineInfo(
                l.ProductId, l.VariantId, l.SupplierId, l.Title, l.Quantity, l.FulfilmentType, l.BillingMode, l.UnitPriceMinor,
                lineDiscounts[i])).ToList());
    }
}
