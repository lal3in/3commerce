using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Entity;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// "Collect at warehouse" checkout (zero shipping, no carrier, records the warehouse address) and the
/// supplier "mark as delivered" action end to end across Ordering (+ its Entity read models). Proves the
/// collect order still posts a balanced sale (trial balance nets 0), a normal order still charges shipping,
/// and mark-delivered is authorized only for the fulfilling supplier / an operator.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class WarehouseCollectAndDeliverTests(Phase3Fixture fixture)
{
    private sealed record CheckoutResponseDto(Guid OrderId, string ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor, long GrossMinor, string Currency, string? Message);
    private sealed record StatusDto(Guid Id, string Status);
    private sealed record SupplierOrderDto(Guid Id, long PublicOrderNumber, string Status, long GrossMinor, string Currency, DateTimeOffset CreatedAt, bool CollectAtWarehouse, string Email, List<SupplierOrderLineDto> Lines);
    private sealed record SupplierOrderLineDto(string Title, int Quantity, string FulfilmentType);

    private static object Collect() => new
    {
        email = "buyer@example.com",
        shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
        collectAtWarehouse = true,
    };

    private static object Shipped() => new
    {
        email = "buyer@example.com",
        shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
    };

    [Fact]
    public async Task Collect_at_warehouse_charges_no_shipping_records_the_warehouse_and_posts_a_balanced_sale()
    {
        var (productId, supplierId) = await fixture.SeedSuppliedProductAsync(
            priceMinor: 5_000, supplierCostMinor: 0, fulfilmentType: FulfilmentType.Warehouse);
        await SeedWarehouseAsync(supplierId, "Demo Supplier", "1 Supplier Way", "Sydney", "2000", "AU");

        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 2 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Collect())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        // Warehouse-fulfilled line + collect → zero shipping; gross = net + 0 + 0.
        Assert.Equal(10_000, order.NetMinor);
        Assert.Equal(0, order.ShippingMinor);
        Assert.Equal(10_000, order.GrossMinor);

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        // The order records collect-at-warehouse and the warehouse address (projected from Entity).
        using (var scope = fixture.Ordering.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var stored = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.OrderId);
            Assert.True(stored.CollectAtWarehouse);
            Assert.Equal("Demo Supplier", stored.WarehouseName);
            Assert.Equal("1 Supplier Way", stored.WarehouseLine1);
            Assert.Equal("Sydney", stored.WarehouseCity);
        }

        // A zero-shipping sale posts no shipping-income line; the ledger still balances.
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task Collect_at_warehouse_is_rejected_when_the_cart_has_no_warehouse_line()
    {
        // A dropship (non-warehouse) product is not eligible for collect — checkout rejects it so the
        // client falls back to a shipped rate; normal shipped/dropship flows are untouched.
        var (productId, _) = await fixture.SeedSuppliedProductAsync(
            priceMinor: 4_000, supplierCostMinor: 0, fulfilmentType: FulfilmentType.Dropship);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });

        var rejected = await shopper.PostAsJsonAsync("/checkout", Collect());
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // The same cart still checks out normally (shipped) and is charged shipping.
        var shipped = (await (await shopper.PostAsJsonAsync("/checkout", Shipped())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        Assert.True(shipped.ShippingMinor > 0, $"expected shipping to be charged, got {shipped.ShippingMinor}");
    }

    [Fact]
    public async Task The_fulfilling_supplier_can_mark_its_order_delivered_but_another_supplier_cannot()
    {
        var (productId, supplierId) = await fixture.SeedSuppliedProductAsync(
            priceMinor: 6_000, supplierCostMinor: 0, fulfilmentType: FulfilmentType.Warehouse);
        await SeedWarehouseAsync(supplierId, "Demo Supplier", "1 Supplier Way", "Sydney", "2000", "AU");

        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Collect())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        // A DIFFERENT supplier may not mark this order delivered (it fulfils none of its lines) → 403.
        using (var stranger = SupplierClient(Guid.CreateVersion7()))
        {
            var forbidden = await stranger.PostAsync($"/orders/supplier/{order.OrderId}/mark-delivered", null);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        // The fulfilling supplier sees the order in its list and marks it delivered → Confirmed → Delivered.
        using var supplier = SupplierClient(supplierId);
        var list = await supplier.GetFromJsonAsync<List<SupplierOrderDto>>("/orders/supplier/me");
        Assert.Contains(list!, o => o.Id == order.OrderId && o.CollectAtWarehouse);

        var marked = await supplier.PostAsync($"/orders/supplier/{order.OrderId}/mark-delivered", null);
        marked.EnsureSuccessStatusCode();
        var status = (await marked.Content.ReadFromJsonAsync<StatusDto>())!;
        Assert.Equal("Delivered", status.Status);

        // Idempotent: a second mark returns Delivered again with no error.
        var again = await supplier.PostAsync($"/orders/supplier/{order.OrderId}/mark-delivered", null);
        again.EnsureSuccessStatusCode();

        // The transition is durable and the money is untouched (ledger still balances).
        await WaitForStatusAsync(shopper, order.OrderId, "Delivered");
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    private async Task SeedWarehouseAsync(Guid supplierId, string name, string line1, string city, string postcode, string country)
    {
        await fixture.PublishAsync(new SupplierWarehouseChanged(
            new Guid("00000000-0000-0000-0000-000000000001"), supplierId, name, line1, null, city, null, postcode, country));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await db.SupplierWarehouseCopies.AnyAsync(w => w.SupplierId == supplierId))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"SupplierWarehouseCopy for {supplierId} did not project.");
    }

    private HttpClient SupplierClient(Guid supplierEntity)
    {
        var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName,
            fixture.MintInternalClaims(Guid.CreateVersion7(), InternalClaimsAuth.SupplierRole, supplierEntity: supplierEntity));
        return client;
    }

    private async Task SimulatePaymentAsync(Guid orderId, long gross)
    {
        await WaitForSagaAsync(orderId);
        using var payments = fixture.Payments.CreateClient();
        var intentId = $"pi_fake_{orderId:N}";
        (await payments.PostAsync($"/dev/simulate-payment/{intentId}?amountMinor={gross}", null)).EnsureSuccessStatusCode();
    }

    private async Task WaitForSagaAsync(Guid orderId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await db.CheckoutStates.AnyAsync(s => s.CorrelationId == orderId))
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Checkout saga for {orderId} did not start.");
    }

    private static async Task WaitForStatusAsync(HttpClient client, Guid orderId, string expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<StatusDto>($"/orders/{orderId}/status");
            if (status?.Status == expected)
            {
                return;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"Order {orderId} did not reach {expected}.");
    }
}
