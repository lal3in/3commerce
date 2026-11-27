using System.Text.Json;
using MassTransit.Serialization;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Support.Infrastructure.Consumers;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane — <see cref="OrderConfirmed.UserId"/> (ADR-0018 amendment 2026-11-27) is APPENDED, on the ADR-0055
/// back-compat pattern: an event published before it existed deserializes with MassTransit's own serializer options to
/// <c>null</c>, and Support's snapshot then has no owner (its customer endpoints fail closed). A positive control proves
/// the same path carries the owner when it is sent.
/// </summary>
public sealed class OrderConfirmedOwnerContractTests
{
    // An OrderConfirmed exactly as Ordering published it before UserId: no "userId" key at all.
    private const string PreOwnerOrderConfirmed = """
        {
          "orderId": "01920000-0000-7000-8000-0000000000a1",
          "tenantId": "00000000-0000-0000-0000-000000000001",
          "email": "legacy@example.com",
          "amountMinor": 2000,
          "currency": "EUR",
          "shipTo": { "name": "Legacy", "line1": "1 Old St", "city": "Paris", "postcode": "75001", "country": "FR", "region": null },
          "lines": [
            { "productId": "01920000-0000-7000-8000-0000000000a2", "variantId": null, "supplierId": null, "title": "Keyboard",
              "quantity": 2, "fulfilmentType": 2, "billingMode": 1, "unitPriceMinor": 1000 }
          ],
          "capturedAt": "2026-11-01T10:00:00+00:00"
        }
        """;

    [Fact]
    public void A_pre_owner_order_confirmed_deserializes_without_an_owner_and_projects_an_ownerless_snapshot()
    {
        var m = JsonSerializer.Deserialize<OrderConfirmed>(PreOwnerOrderConfirmed, SystemTextJsonMessageSerializer.Options)!;

        Assert.Null(m.UserId);
        Assert.Equal(2000, m.AmountMinor);
        var snapshot = OrderSnapshotConsumer.Snapshot(m);
        Assert.Null(snapshot.UserId);
        Assert.Equal(2, Assert.Single(snapshot.Lines).Quantity);
    }

    [Fact]
    public void The_owner_round_trips_and_lands_on_the_snapshot()
    {
        var owner = Guid.Parse("01920000-0000-7000-8000-0000000000a3");
        var sent = new OrderConfirmed(
            Guid.CreateVersion7(), Guid.Parse("00000000-0000-0000-0000-000000000001"), "owner@example.com", 1000, "EUR",
            new ShipToInfo("Owner", "1 New St", "Paris", "75001", "FR"),
            [new OrderLineInfo(Guid.CreateVersion7(), null, null, "Lamp", 1, FulfilmentType.Warehouse, BillingMode.OneTime, 1000)],
            UserId: owner);

        var json = JsonSerializer.Serialize(sent, SystemTextJsonMessageSerializer.Options);
        var received = JsonSerializer.Deserialize<OrderConfirmed>(json, SystemTextJsonMessageSerializer.Options)!;

        Assert.Equal(owner, received.UserId);
        Assert.Equal(owner, OrderSnapshotConsumer.Snapshot(received).UserId);
    }
}
