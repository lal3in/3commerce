using ThreeCommerce.Fulfillment.Domain.Carriers;

namespace ThreeCommerce.Fulfillment.Infrastructure.Carriers;

/// <summary>
/// Produces shipping rates for a tenant/storefront (mt4_4): resolves the configured default carrier
/// (mt4_3) and asks its rate adapter. Falls back to the keyless Fake so a quote always returns in
/// dev/CI even before a real carrier is configured.
/// </summary>
public sealed class ShippingQuoteService(CarrierService carriers, CarrierRegistry registry, FakeCarrierProvider fake)
{
    public async Task<IReadOnlyList<CarrierRate>> QuoteAsync(
        Guid tenantId, Guid? storefrontId, RateRequest request, CancellationToken ct)
    {
        var integration = await carriers.ResolveDefaultAsync(tenantId, storefrontId, ct);
        var provider = integration is null ? null : registry.Rates(integration.Carrier);
        provider ??= fake;
        return await provider.GetRatesAsync(request, ct);
    }
}
