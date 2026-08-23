using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

public class CheckoutTests
{
    [Fact]
    public void CheckoutAttempt_creates_confirmed_order_after_payment_success()
    {
        var storefrontId = Guid.CreateVersion7();
        var attempt = NewAttempt(storefrontId);
        var order = attempt.ToOrder(orderNumber: 1000, DateTimeOffset.UtcNow);

        Assert.Equal(attempt.Id, order.Id);
        Assert.Equal(storefrontId, order.StorefrontId);
        Assert.Equal(1000, order.PublicOrderNumber);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(attempt.GrossMinor, order.GrossMinor);
        Assert.Single(order.Lines);
    }

    [Fact]
    public void CheckoutAttempt_prevents_non_awaiting_attempt_from_becoming_order()
    {
        var attempt = NewAttempt(Guid.CreateVersion7());
        attempt.Status = CheckoutAttemptStatus.Cancelled;

        Assert.Throws<OrderingRuleException>(() => attempt.ToOrder(1000, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CheckoutAttempt_carries_collect_at_warehouse_and_warehouse_address_onto_the_order()
    {
        var collectAttempt = new CheckoutAttempt
        {
            Id = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            StorefrontId = Guid.CreateVersion7(),
            Email = "buyer@example.test",
            NetMinor = 1000,
            ShippingMinor = 0,
            GrossMinor = 1000,
            Currency = "AUD",
            PaymentIntentId = "pi_fake",
            ShipName = "Buyer",
            ShipLine1 = "1 Street",
            ShipCity = "Sydney",
            ShipPostcode = "2000",
            ShipCountry = "AU",
            CollectAtWarehouse = true,
            WarehouseName = "Demo Supplier",
            WarehouseLine1 = "1 Supplier Way",
            WarehouseCity = "Sydney",
            WarehousePostcode = "2000",
            WarehouseCountry = "AU",
            CreatedAt = DateTimeOffset.UtcNow,
            Lines =
            [
                new CheckoutAttemptLine
                {
                    Id = Guid.CreateVersion7(),
                    CheckoutAttemptId = Guid.CreateVersion7(),
                    ProductId = Guid.CreateVersion7(),
                    Title = "Warehouse product",
                    UnitPriceMinor = 1000,
                    Quantity = 1,
                    FulfilmentType = FulfilmentType.Warehouse,
                },
            ],
        };

        var order = collectAttempt.ToOrder(1000, DateTimeOffset.UtcNow);

        Assert.True(order.CollectAtWarehouse);
        Assert.Equal(0, order.ShippingMinor);
        Assert.Equal("Demo Supplier", order.WarehouseName);
        Assert.Equal("1 Supplier Way", order.WarehouseLine1);
        Assert.Equal("Sydney", order.WarehouseCity);
        Assert.Equal("AU", order.WarehouseCountry);
    }

    [Fact]
    public void OrderNumberSequence_allocates_per_storefront_order_numbers()
    {
        var sequence = new OrderNumberSequence { StorefrontId = Guid.CreateVersion7() };

        Assert.Equal(1000, sequence.ReserveNext());
        Assert.Equal(1001, sequence.ReserveNext());
    }

    private static CheckoutAttempt NewAttempt(Guid storefrontId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Guid.CreateVersion7(),
        StorefrontId = storefrontId,
        UserId = Guid.CreateVersion7(),
        Email = "buyer@example.test",
        NetMinor = 1000,
        ShippingMinor = 499,
        TaxMinor = 0,
        GrossMinor = 1499,
        Currency = "AUD",
        PaymentIntentId = "pi_fake",
        ShipName = "Buyer",
        ShipLine1 = "1 Street",
        ShipCity = "Sydney",
        ShipPostcode = "2000",
        ShipCountry = "AU",
        CreatedAt = DateTimeOffset.UtcNow,
        Lines =
        [
            new CheckoutAttemptLine
            {
                Id = Guid.CreateVersion7(),
                CheckoutAttemptId = Guid.CreateVersion7(),
                ProductId = Guid.CreateVersion7(),
                Title = "Product",
                UnitPriceMinor = 1000,
                Quantity = 1,
                FulfilmentType = FulfilmentType.Dropship,
            },
        ],
    };
}
