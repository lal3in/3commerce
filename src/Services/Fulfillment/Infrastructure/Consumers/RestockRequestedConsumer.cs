using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Fulfillment;

namespace ThreeCommerce.Fulfillment.Infrastructure.Consumers;

/// <summary>Restocks returned items into inventory on RestockRequested (mt4_8). Idempotent by reference.</summary>
public sealed class RestockRequestedConsumer(ReservationService reservations) : IConsumer<RestockRequested>
{
    public Task Consume(ConsumeContext<RestockRequested> context)
    {
        var m = context.Message;
        var lines = m.Items
            .Select(i => new RestockLine(i.ProductId, i.VariantId, i.LocationId, i.Quantity))
            .ToList();
        return reservations.RestockAsync(m.TenantId, m.ReferenceId, lines, context.CancellationToken);
    }
}
