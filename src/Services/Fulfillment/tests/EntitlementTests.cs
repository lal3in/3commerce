using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Tests;

public class EntitlementTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(FulfilmentType.DigitalDownload, EntitlementType.Download)]
    [InlineData(FulfilmentType.Subscription, EntitlementType.Subscription)]
    [InlineData(FulfilmentType.Usage, EntitlementType.ApiAccess)]
    [InlineData(FulfilmentType.ManualService, EntitlementType.ServiceAccess)]
    public void Issue_maps_digital_fulfilment_to_an_active_entitlement(FulfilmentType fulfilment, EntitlementType expected)
    {
        var entitlement = Entitlement.Issue(Guid.NewGuid(), Guid.NewGuid(), "BUYER@Example.com", Guid.NewGuid(), null, fulfilment, Now);
        Assert.NotNull(entitlement);
        Assert.Equal(expected, entitlement!.Type);
        Assert.Equal("buyer@example.com", entitlement.CustomerEmail); // normalised
        Assert.True(entitlement.IsActive);
    }

    [Theory]
    [InlineData(FulfilmentType.Warehouse)]
    [InlineData(FulfilmentType.Dropship)]
    [InlineData(FulfilmentType.Unassigned)]
    public void Issue_returns_null_for_a_physical_line(FulfilmentType fulfilment) =>
        Assert.Null(Entitlement.Issue(Guid.NewGuid(), Guid.NewGuid(), "b@x.com", Guid.NewGuid(), null, fulfilment, Now));

    [Fact]
    public void Lifecycle_transitions()
    {
        var entitlement = Entitlement.Issue(Guid.NewGuid(), Guid.NewGuid(), "b@x.com", Guid.NewGuid(), null, FulfilmentType.Subscription, Now)!;
        entitlement.Suspend();
        Assert.Equal(EntitlementStatus.Suspended, entitlement.Status);
        entitlement.Cancel();
        Assert.Equal(EntitlementStatus.Cancelled, entitlement.Status);
    }
}
