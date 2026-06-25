using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Tests;

public class InventoryReservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);

    private static InventoryItem Stocked(int onHand) =>
        InventoryItem.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, onHand, Now);

    [Fact]
    public void Reserve_reduces_available_and_increases_reserved()
    {
        var item = Stocked(10);
        item.Reserve(4, Now);
        Assert.Equal(4, item.QuantityReserved);
        Assert.Equal(10, item.QuantityOnHand);
        Assert.Equal(6, item.Available);
    }

    [Fact]
    public void Reserve_rejects_more_than_available_and_nonpositive()
    {
        var item = Stocked(3);
        Assert.Throws<FulfillmentRuleException>(() => item.Reserve(4, Now));
        Assert.Throws<FulfillmentRuleException>(() => item.Reserve(0, Now));
    }

    [Fact]
    public void Release_restores_available_and_clamps_at_zero()
    {
        var item = Stocked(10);
        item.Reserve(5, Now);
        item.Release(2, Now);
        Assert.Equal(3, item.QuantityReserved);
        Assert.Equal(7, item.Available);
        item.Release(100, Now); // over-release clamps
        Assert.Equal(0, item.QuantityReserved);
    }

    [Fact]
    public void ConfirmReservation_consumes_on_hand_and_drops_the_hold()
    {
        var item = Stocked(10);
        item.Reserve(3, Now);
        item.ConfirmReservation(3, Now);
        Assert.Equal(7, item.QuantityOnHand);
        Assert.Equal(0, item.QuantityReserved);
        Assert.Equal(7, item.Available);
    }

    [Fact]
    public void Confirm_without_a_hold_just_decrements_on_hand()
    {
        var item = Stocked(10);
        item.ConfirmReservation(4, Now); // reserved is 0
        Assert.Equal(6, item.QuantityOnHand);
        Assert.Equal(0, item.QuantityReserved);
    }
}
