using Microsoft.Extensions.Configuration;
using ThreeCommerce.Fulfillment.Domain.Carriers;

namespace ThreeCommerce.Fulfillment.Infrastructure.Carriers;

/// <summary>
/// Resolves the parcel to rate against (mt4_11). Carrier rate APIs need weight + dimensions, but
/// seeded / unmapped SKUs may have none — a parcel with no usable weight falls back to a configurable
/// default (`Shipping:DefaultParcel:*`) so a quote always returns. An explicit request parcel wins.
/// </summary>
public sealed class ParcelResolver(IConfiguration config)
{
    public Parcel Default => new(
        Value("Shipping:DefaultParcel:WeightGrams", 500),
        Value("Shipping:DefaultParcel:LengthMm", 200),
        Value("Shipping:DefaultParcel:WidthMm", 150),
        Value("Shipping:DefaultParcel:HeightMm", 100));

    public Parcel Resolve(Parcel parcel) => parcel.WeightGrams > 0 ? parcel : Default;

    private int Value(string key, int fallback) =>
        int.TryParse(config[key], out var value) && value > 0 ? value : fallback;
}
