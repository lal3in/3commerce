using ThreeCommerce.Fulfillment.Domain.Carriers;

namespace ThreeCommerce.Fulfillment.Infrastructure.Carriers;

/// <summary>
/// Produces shipping rates for a tenant/storefront (mt4_4): resolves the configured default carrier
/// (mt4_3) and asks its rate adapter. Falls back to the keyless Fake so a quote always returns in
/// dev/CI even before a real carrier is configured.
/// </summary>
public sealed class ShippingQuoteService(
    CarrierService carriers, CarrierRegistry registry, FakeCarrierProvider fake, TimeProvider clock)
{
    public async Task<IReadOnlyList<CarrierRate>> QuoteAsync(
        Guid tenantId, Guid? storefrontId, RateRequest request, CancellationToken ct)
    {
        var integration = await carriers.ResolveDefaultAsync(tenantId, storefrontId, ct);
        var provider = integration is null ? null : registry.Rates(integration.Carrier);
        provider ??= fake;

        var rates = await provider.GetRatesAsync(request, ct);
        // Fallback (mt4_6): if the configured carrier can't quote this request, use the Fake so a
        // quote always returns rather than blocking checkout.
        if (rates.Count == 0 && provider != fake)
        {
            rates = await fake.GetRatesAsync(request, ct);
        }

        return rates;
    }

    /// <summary>
    /// Revalidate a selected quote before payment (mt4_6): re-quote and compare. A quote past its
    /// expiry is rejected without re-quoting; a vanished service is Unavailable; a changed amount is
    /// PriceChanged (with the current rate) so checkout can re-confirm.
    /// </summary>
    public async Task<RevalidationResult> RevalidateAsync(
        Guid tenantId, Guid? storefrontId, RateRequest request, string selectedService,
        long expectedAmountMinor, DateTimeOffset expiresAt, CancellationToken ct)
    {
        if (clock.GetUtcNow() > expiresAt)
        {
            return new RevalidationResult(QuoteRevalidation.Expired, null);
        }

        var rates = await QuoteAsync(tenantId, storefrontId, request with { Service = selectedService }, ct);
        var rate = rates.FirstOrDefault(r => r.Service == selectedService);
        if (rate is null)
        {
            return new RevalidationResult(QuoteRevalidation.Unavailable, null);
        }

        return rate.AmountMinor == expectedAmountMinor
            ? new RevalidationResult(QuoteRevalidation.Valid, rate)
            : new RevalidationResult(QuoteRevalidation.PriceChanged, rate);
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

public enum QuoteRevalidation { Valid, Expired, PriceChanged, Unavailable }

/// <summary>Outcome of revalidating a selected quote before payment (mt4_6).</summary>
public sealed record RevalidationResult(QuoteRevalidation Outcome, CarrierRate? CurrentRate);
