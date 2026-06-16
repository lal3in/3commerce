using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Fulfillment.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>FR (Fulfillment): a confirmed order becomes shipments grouped by fulfillment source (ADR-0003).</summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public class FulfillmentFlowTests(Phase4Fixture fixture)
{
    [Fact]
    public async Task OrderConfirmed_creates_shipments_grouped_by_source()
    {
        var orderId = Guid.CreateVersion7();
        var lines = new List<OrderLineInfo>
        {
            new(Guid.CreateVersion7(), "Item A", 1, "Unassigned", 1000),
            new(Guid.CreateVersion7(), "Item B", 2, "Unassigned", 1500),
            new(Guid.CreateVersion7(), "Item C", 1, "OwnWarehouse", 2000),
        };

        await fixture.PublishAsync(new OrderConfirmed(orderId, "buyer@example.com", 9999, "EUR", lines));

        var shipments = await WaitForShipmentsAsync(orderId);
        // Two distinct sources → two shipments; the Unassigned one has both its lines.
        Assert.Equal(2, shipments.Count);
        Assert.Contains(shipments, s => s.Source == "Unassigned" && s.LineCount == 2);
        Assert.Contains(shipments, s => s.Source == "OwnWarehouse" && s.LineCount == 1);
    }

    [Fact]
    public async Task Duplicate_OrderConfirmed_does_not_duplicate_shipments()
    {
        var orderId = Guid.CreateVersion7();
        var lines = new List<OrderLineInfo> { new(Guid.CreateVersion7(), "X", 1, "Unassigned", 100) };

        await fixture.PublishAsync(new OrderConfirmed(orderId, "b@example.com", 100, "EUR", lines));
        await fixture.PublishAsync(new OrderConfirmed(orderId, "b@example.com", 100, "EUR", lines));

        await Task.Delay(2000);
        var shipments = await WaitForShipmentsAsync(orderId);
        Assert.Single(shipments);
    }

    private sealed record ShipmentInfo(string Source, int LineCount);

    private async Task<List<ShipmentInfo>> WaitForShipmentsAsync(Guid orderId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Fulfillment.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>();
            var shipments = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .Include(db.Shipments, s => s.Lines)
                    .Where(s => s.OrderId == orderId));
            if (shipments.Count > 0)
            {
                return shipments.Select(s => new ShipmentInfo(s.FulfillmentSource, s.Lines.Count)).ToList();
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"No shipments created for order {orderId}.");
    }
}
