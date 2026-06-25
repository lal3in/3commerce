using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Tests;

public class InventoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();

    [Fact]
    public void Location_create_sets_active_defaults()
    {
        var address = Guid.NewGuid();
        var location = InventoryLocation.Create(Tenant, Entity, address, "  Main DC  ", LocationKind.TenantWarehouse, Now);

        Assert.NotEqual(Guid.Empty, location.Id);
        Assert.Equal(Tenant, location.TenantId);
        Assert.Equal(Entity, location.EntityId);
        Assert.Equal(address, location.AddressId);
        Assert.Equal("Main DC", location.Name); // trimmed
        Assert.Equal(LocationKind.TenantWarehouse, location.Kind);
        Assert.Equal(LocationStatus.Active, location.Status);
        Assert.True(location.IsActive);
    }

    [Fact]
    public void Location_create_requires_owning_entity()
    {
        var ex = Assert.Throws<FulfillmentRuleException>(
            () => InventoryLocation.Create(Tenant, Guid.Empty, null, "DC", LocationKind.SupplierDirect, Now));
        Assert.Contains("entity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Location_create_requires_name(string name) =>
        Assert.Throws<FulfillmentRuleException>(
            () => InventoryLocation.Create(Tenant, Entity, null, name, LocationKind.TenantWarehouse, Now));

    [Fact]
    public void Location_deactivate_then_activate_toggles_status()
    {
        var location = InventoryLocation.Create(Tenant, Entity, null, "DC", LocationKind.ThirdPartyForwarder, Now);
        location.Deactivate();
        Assert.False(location.IsActive);
        location.Activate();
        Assert.True(location.IsActive);
    }

    [Fact]
    public void Item_create_requires_positive_onhand_and_ids()
    {
        var loc = Guid.NewGuid();
        Assert.Throws<FulfillmentRuleException>(() => InventoryItem.Create(Tenant, loc, Product, null, -1, Now));
        Assert.Throws<FulfillmentRuleException>(() => InventoryItem.Create(Tenant, Guid.Empty, Product, null, 5, Now));
        Assert.Throws<FulfillmentRuleException>(() => InventoryItem.Create(Guid.Empty, loc, Product, null, 5, Now));
    }

    [Fact]
    public void Item_available_is_onhand_minus_reserved()
    {
        var item = InventoryItem.Create(Tenant, Guid.NewGuid(), Product, Guid.NewGuid(), 12, Now);
        Assert.Equal(12, item.QuantityOnHand);
        Assert.Equal(0, item.QuantityReserved);
        Assert.Equal(12, item.Available);
    }

    [Fact]
    public void SetOnHand_overwrites_quantity_and_stamps_time()
    {
        var item = InventoryItem.Create(Tenant, Guid.NewGuid(), Product, null, 3, Now);
        var later = Now.AddHours(1);
        item.SetOnHand(40, later);
        Assert.Equal(40, item.QuantityOnHand);
        Assert.Equal(later, item.UpdatedAt);
    }

    [Fact]
    public void SetOnHand_rejects_negative() =>
        Assert.Throws<FulfillmentRuleException>(
            () => InventoryItem.Create(Tenant, Guid.NewGuid(), Product, null, 5, Now).SetOnHand(-1, Now));

    [Fact]
    public void Adjust_applies_delta_and_guards_below_zero()
    {
        var item = InventoryItem.Create(Tenant, Guid.NewGuid(), Product, null, 10, Now);
        item.Adjust(5, Now);
        Assert.Equal(15, item.QuantityOnHand);
        item.Adjust(-15, Now);
        Assert.Equal(0, item.QuantityOnHand);
        Assert.Throws<FulfillmentRuleException>(() => item.Adjust(-1, Now));
    }
}
