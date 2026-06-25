using Microsoft.Extensions.Configuration;
using ThreeCommerce.Fulfillment.Domain.Carriers;
using ThreeCommerce.Fulfillment.Infrastructure.Carriers;

namespace ThreeCommerce.Fulfillment.Tests;

public class ParcelResolverTests
{
    private static ParcelResolver Resolver(params (string Key, string Value)[] config)
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value)));
        return new ParcelResolver(builder.Build());
    }

    [Fact]
    public void Missing_weight_falls_back_to_the_built_in_default()
    {
        var resolved = Resolver().Resolve(new Parcel(0, 0, 0, 0));
        Assert.Equal(500, resolved.WeightGrams); // built-in default
        Assert.Equal(200, resolved.LengthMm);
    }

    [Fact]
    public void Explicit_parcel_with_weight_is_kept()
    {
        var parcel = new Parcel(1234, 10, 20, 30);
        Assert.Equal(parcel, Resolver().Resolve(parcel));
    }

    [Fact]
    public void Configured_default_parcel_overrides_the_built_in_one()
    {
        var resolved = Resolver(
            ("Shipping:DefaultParcel:WeightGrams", "750"),
            ("Shipping:DefaultParcel:LengthMm", "300")).Resolve(new Parcel(0, 0, 0, 0));
        Assert.Equal(750, resolved.WeightGrams);
        Assert.Equal(300, resolved.LengthMm);
    }
}
