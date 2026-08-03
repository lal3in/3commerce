using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;

namespace ThreeCommerce.Fulfillment.Infrastructure.Consumers;

/// <summary>
/// When a storefront is duplicated, copy the source storefront's carrier accounts (config + credential
/// reference) onto the new storefront so the duplicate ships with the same carriers. Idempotent by
/// (tenant, new storefront) — a redelivered message is a no-op.
/// </summary>
public sealed class StorefrontDuplicatedConsumer(CarrierService carriers)
    : IConsumer<StorefrontDuplicated>
{
    public Task Consume(ConsumeContext<StorefrontDuplicated> context)
    {
        var m = context.Message;
        return carriers.CloneStorefrontCarriersAsync(
            m.TenantId, m.SourceStorefrontId, m.NewStorefrontId, context.CancellationToken);
    }
}
