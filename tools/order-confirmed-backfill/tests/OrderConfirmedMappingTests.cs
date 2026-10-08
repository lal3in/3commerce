using System.Text.Json;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;
using static ThreeCommerce.Tools.OrderConfirmedBackfill.Tests.BackfillFixtures;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

/// <summary>
/// A backfilled <see cref="OrderConfirmed"/> must be the event the live path published. On confirmation
/// <c>OrderStatusConsumer</c> turns the checkout attempt into the order (<c>CheckoutAttempt.ToOrder</c>) and publishes
/// <see cref="OrderConfirmedFactory.From"/> of that in-memory aggregate; the backfill reloads the order from Ordering's
/// database (<see cref="OrderSource"/>) and runs the same factory. Equal up to line order — the line order of a
/// multi-line order follows its line ids in both paths, which are not ordered within one millisecond.
/// </summary>
public class OrderConfirmedMappingTests
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Supplier = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Shelf = Guid.Parse("00000000-0000-0000-0000-000000000c01");
    private static readonly Guid Dropped = Guid.Parse("00000000-0000-0000-0000-000000000c02");
    private static readonly Guid Download = Guid.Parse("00000000-0000-0000-0000-000000000c03");
    private static readonly Guid Variant = Guid.Parse("00000000-0000-0000-0000-000000000d01");

    /// <summary>Three supply types, a line promotion AND a storefront-wide discount, a region, a variant.</summary>
    private static CheckoutAttempt Attempt()
    {
        var id = Guid.CreateVersion7();
        return new CheckoutAttempt
        {
            Id = id,
            TenantId = Tenant,
            StorefrontId = Guid.NewGuid(),
            Email = "shopper@example.test",
            Status = CheckoutAttemptStatus.AwaitingPayment,
            NetMinor = 4900,
            ShippingMinor = 499,
            TaxMinor = 490,
            DiscountMinor = 600, // 100 promotion on the shelf line + 500 storefront-wide
            PromotionDiscountMinor = 100,
            GrossMinor = 5889,
            Currency = "AUD",
            PaymentIntentId = "pi_test",
            ShipName = "Shopper",
            ShipLine1 = "1 Example St",
            ShipCity = "Sydney",
            ShipRegion = "NSW",
            ShipPostcode = "2000",
            ShipCountry = "AU",
            CreatedAt = T0.AddMinutes(-3),
            Lines =
            [
                Line(id, Shelf, Variant, "Shelf item", 2, 1000, 100, FulfilmentType.Warehouse, null),
                Line(id, Dropped, null, "Dropshipped item", 1, 3000, 0, FulfilmentType.Dropship, Supplier),
                Line(id, Download, null, "Download", 1, 500, 0, FulfilmentType.DigitalDownload, null),
            ],
        };
    }

    private static CheckoutAttemptLine Line(
        Guid attempt, Guid product, Guid? variant, string title, int qty, long unit, long promo, FulfilmentType type, Guid? supplier) => new()
        {
            Id = Guid.CreateVersion7(),
            CheckoutAttemptId = attempt,
            ProductId = product,
            VariantId = variant,
            Title = title,
            Quantity = qty,
            UnitPriceMinor = unit,
            DiscountMinor = promo,
            FulfilmentType = type,
            SupplierId = supplier,
        };

    [Fact]
    public async Task A_reloaded_order_rebuilds_the_event_the_live_confirmation_published()
    {
        // The live path, exactly: attempt → order (in memory) → factory.
        var live = Attempt().ToOrder(1042, T0);
        var published = OrderConfirmedFactory.From(live);

        // The backfill path: the persisted order, read back by the tool's loader → the same factory.
        var db = nameof(A_reloaded_order_rebuilds_the_event_the_live_confirmation_published);
        await using (var ordering = OrderingDb(db))
        {
            ordering.Orders.Add(live);
            await ordering.SaveChangesAsync();
        }

        await using var reader = OrderingDb(db);
        var reloaded = Assert.Single(await new OrderSource(reader, new PassThroughTenantScope()).ConfirmedOrdersAsync(Tenant, default));
        var backfilled = OrderConfirmedFactory.From(reloaded);

        Assert.Equal(Canonical(published), Canonical(backfilled));
    }

    [Fact]
    public void The_event_carries_tenant_ship_to_supply_and_the_whole_discount_per_line()
    {
        var e = OrderConfirmedFactory.From(Attempt().ToOrder(1042, T0));

        Assert.Equal(Tenant, e.TenantId);
        Assert.Equal("shopper@example.test", e.Email);
        Assert.Equal(5889, e.AmountMinor);
        Assert.Equal("AUD", e.Currency);
        Assert.Equal(new ShipToInfo("Shopper", "1 Example St", "Sydney", "2000", "AU", "NSW"), e.ShipTo);

        var lines = e.Lines.ToDictionary(l => l.ProductId);
        Assert.Equal(new OrderLineInfo(Shelf, Variant, null, "Shelf item", 2, FulfilmentType.Warehouse, BillingMode.OneTime, 1000, 282), lines[Shelf]);
        Assert.Equal(new OrderLineInfo(Dropped, null, Supplier, "Dropshipped item", 1, FulfilmentType.Dropship, BillingMode.OneTime, 3000, 273), lines[Dropped]);
        Assert.Equal(new OrderLineInfo(Download, null, null, "Download", 1, FulfilmentType.DigitalDownload, BillingMode.OneTime, 500, 45), lines[Download]);
        Assert.Equal(600, e.Lines.Sum(l => l.DiscountMinor)); // allocations sum to the order's discount exactly
    }

    /// <summary>JSON of the event with lines in a fixed order (records compare lists by reference).</summary>
    private static string Canonical(OrderConfirmed e) =>
        JsonSerializer.Serialize(e with { Lines = e.Lines.OrderBy(l => l.ProductId).ToList() });
}
