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

    /// <summary>Quote each shipment group of an order independently (mt4_5) — one order, multiple shipments.</summary>
    public async Task<IReadOnlyList<GroupQuote>> QuoteGroupsAsync(
        Guid tenantId, Guid? storefrontId, ShipAddress destination,
        IReadOnlyList<(string SourceKey, ShipAddress Origin, Parcel Parcel)> groups, CancellationToken ct)
    {
        var result = new List<GroupQuote>();
        foreach (var group in groups)
        {
            var rates = await QuoteAsync(tenantId, storefrontId, new RateRequest(group.Origin, destination, group.Parcel), ct);
            result.Add(new GroupQuote(group.SourceKey, rates));
        }

        return result;
    }
}

/// <summary>Shipping options for one shipment group (mt4_5). The customer selects one per group.</summary>
public sealed record GroupQuote(string SourceKey, IReadOnlyList<CarrierRate> Rates);
