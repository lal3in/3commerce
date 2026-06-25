using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Tests;

public class CarrierIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void Configure_starts_in_draft_at_tenant_level()
    {
        var c = CarrierIntegration.Configure(Tenant, null, CarrierCode.AustraliaPost, "secret-ref", Now);
        Assert.Equal(CarrierIntegrationStatus.Draft, c.Status);
        Assert.Null(c.StorefrontId);
        Assert.False(c.IsDefault);
        Assert.False(c.IsUsable);
    }

    [Fact]
    public void Real_carrier_cannot_activate_without_a_credential_reference()
    {
        var c = CarrierIntegration.Configure(Tenant, null, CarrierCode.Dhl, null, Now);
        Assert.Throws<FulfillmentRuleException>(() => c.Activate(Now));
        c.SetCredentialRef("dhl-key", Now);
        c.Activate(Now);
        Assert.True(c.IsUsable);
    }

    [Fact]
    public void Fake_carrier_activates_without_credentials()
    {
        var c = CarrierIntegration.Configure(Tenant, null, CarrierCode.Fake, null, Now);
        c.Activate(Now);
        Assert.True(c.IsUsable);
    }

    [Fact]
    public void Only_an_active_carrier_can_be_default_and_disable_clears_it()
    {
        var c = CarrierIntegration.Configure(Tenant, null, CarrierCode.Fake, null, Now);
        Assert.Throws<FulfillmentRuleException>(() => c.MarkDefault(Now)); // still draft
        c.Activate(Now);
        c.MarkDefault(Now);
        Assert.True(c.IsDefault);
        c.Disable(Now);
        Assert.False(c.IsDefault);
        Assert.Equal(CarrierIntegrationStatus.Disabled, c.Status);
    }
}
