using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Entitlement.Domain;
using ThreeCommerce.Entitlement.Infrastructure;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Fulfillment.Infrastructure;
using EntitlementRecord = ThreeCommerce.Entitlement.Domain.Entitlement;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt7_2: a confirmed digital line issues an entitlement (not a shipment); mixed orders do both.</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class DigitalFulfilmentTests(Phase4Fixture fixture)
{
    private static readonly ShipToInfo Ship = new("Buyer", "1 St", "Sydney", "2000", "AU");

    private async Task<List<EntitlementRecord>> EntitlementsAsync(Guid tenant, Guid orderId)
    {
        using var scope = fixture.Entitlement.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EntitlementService>().ListAsync(tenant, orderId, null, default);
    }

    private async Task<bool> HasShipmentAsync(Guid orderId)
    {
        using var scope = fixture.Fulfillment.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>().Shipments.AnyAsync(s => s.OrderId == orderId);
    }

    private async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> done, string failure)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            await Task.Delay(300);
        }

        throw new Xunit.Sdk.XunitException(failure);
    }

    [Fact]
    public async Task Digital_line_issues_an_entitlement_and_creates_no_shipment()
    {
        var tenant = Guid.NewGuid();
        var product = Guid.NewGuid();
        var orderId = Guid.CreateVersion7();

        var lines = new List<OrderLineInfo>
        {
            new(product, null, null, "E-book", 1, FulfilmentType.DigitalDownload, BillingMode.OneTime, 1500),
        };
        await fixture.PublishAsync(new OrderConfirmed(orderId, tenant, "buyer@example.com", 1500, "EUR", Ship, lines));

        var entitlements = await PollAsync(() => EntitlementsAsync(tenant, orderId), e => e.Count == 1, "No entitlement was issued.");
        Assert.Equal(EntitlementType.Download, entitlements[0].Type);
        Assert.Equal("buyer@example.com", entitlements[0].CustomerEmail);
        Assert.False(await HasShipmentAsync(orderId)); // digital does not ship
    }

    [Fact]
    public async Task Non_physical_matrix_lines_issue_the_expected_entitlements_and_no_shipments()
    {
        var tenant = Guid.NewGuid();
        var orderId = Guid.CreateVersion7();
        var lines = new List<OrderLineInfo>
        {
            new(Guid.NewGuid(), null, null, "E-book", 1, FulfilmentType.DigitalDownload, BillingMode.OneTime, 1200),
            new(Guid.NewGuid(), null, null, "Monthly plan", 1, FulfilmentType.Subscription, BillingMode.Recurring, 990),
            new(Guid.NewGuid(), null, null, "API meter", 3, FulfilmentType.Usage, BillingMode.Metered, 0),
            new(Guid.NewGuid(), null, null, "Setup service", 1, FulfilmentType.ManualService, BillingMode.OneTime, 7500),
        };

        await fixture.PublishAsync(new OrderConfirmed(orderId, tenant, "matrix@example.com", 9690, "EUR", Ship, lines));

        var entitlements = await PollAsync(() => EntitlementsAsync(tenant, orderId), e => e.Count == 4, "Matrix entitlements were not issued.");
        Assert.Contains(entitlements, e => e.Type == EntitlementType.Download);
        Assert.Contains(entitlements, e => e.Type == EntitlementType.Subscription);
        Assert.Contains(entitlements, e => e.Type == EntitlementType.ApiAccess);
        Assert.Contains(entitlements, e => e.Type == EntitlementType.ServiceAccess);
        Assert.False(await HasShipmentAsync(orderId));
    }

    [Fact]
    public async Task Mixed_order_ships_physical_and_entitles_digital()
    {
        var tenant = Guid.NewGuid();
        var warehouseProduct = Guid.NewGuid();
        var digitalProduct = Guid.NewGuid();
        var orderId = Guid.CreateVersion7();

        using (var scope = fixture.Fulfillment.Services.CreateScope())
        {
            var inventory = scope.ServiceProvider.GetRequiredService<InventoryService>();
            var location = await inventory.CreateLocationAsync(tenant, Guid.NewGuid(), null, "DC", LocationKind.TenantWarehouse, default);
            await inventory.SetStockAsync(tenant, location.Id, warehouseProduct, null, 5, default);
        }

        var lines = new List<OrderLineInfo>
        {
            new(warehouseProduct, null, null, "Widget", 1, FulfilmentType.Warehouse, BillingMode.OneTime, 1000),
            new(digitalProduct, null, null, "Licence", 1, FulfilmentType.Subscription, BillingMode.OneTime, 2000),
        };
        await fixture.PublishAsync(new OrderConfirmed(orderId, tenant, "buyer@example.com", 3000, "EUR", Ship, lines));

        await PollAsync(() => HasShipmentAsync(orderId), shipped => shipped, "Physical line did not ship.");
        var entitlements = await EntitlementsAsync(tenant, orderId);
        Assert.Single(entitlements); // only the digital line
        Assert.Equal(EntitlementType.Subscription, entitlements[0].Type);
    }
}
