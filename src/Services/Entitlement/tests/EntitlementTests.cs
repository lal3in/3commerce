using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using EntitlementRecord = ThreeCommerce.Entitlement.Domain.Entitlement;

namespace ThreeCommerce.Entitlement.Tests;

public class EntitlementTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Digital_line_issues_an_entitlement_with_normalized_email()
    {
        var e = EntitlementRecord.Issue(Guid.NewGuid(), Guid.NewGuid(), "  Alice@Example.COM ", Guid.NewGuid(), null, FulfilmentType.DigitalDownload, Now);
        Assert.NotNull(e);
        Assert.Equal(Domain.EntitlementType.Download, e!.Type);
        Assert.Equal("alice@example.com", e.CustomerEmail);
        Assert.True(e.IsActive);
    }

    [Fact]
    public void Physical_line_issues_no_entitlement()
    {
        Assert.Null(EntitlementRecord.Issue(Guid.NewGuid(), Guid.NewGuid(), "a@b.com", Guid.NewGuid(), null, FulfilmentType.Warehouse, Now));
    }

    [Theory]
    [InlineData(FulfilmentType.Subscription, Domain.EntitlementType.Subscription)]
    [InlineData(FulfilmentType.Usage, Domain.EntitlementType.ApiAccess)]
    [InlineData(FulfilmentType.ManualService, Domain.EntitlementType.ServiceAccess)]
    public void TypeFor_maps_digital_fulfilment_types(FulfilmentType fulfilment, Domain.EntitlementType expected)
    {
        Assert.Equal(expected, EntitlementRecord.TypeFor(fulfilment));
    }
}
