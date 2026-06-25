using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Tests;

public class UsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);

    private static UsageBalance Balance() => UsageBalance.Create(Guid.NewGuid(), "BUYER@Example.com", MeterType.Token, Now);

    [Fact]
    public void Create_normalises_email_and_starts_empty()
    {
        var balance = Balance();
        Assert.Equal("buyer@example.com", balance.CustomerEmail);
        Assert.Equal(0, balance.UsedQuantity);
        Assert.Equal(0, balance.RemainingQuantity);
    }

    [Fact]
    public void Provision_sets_included_and_remaining()
    {
        var balance = Balance();
        balance.Provision(1000, null, Now);
        Assert.Equal(1000, balance.IncludedQuantity);
        Assert.Equal(1000, balance.RemainingQuantity);
    }

    [Fact]
    public void Add_increments_used_and_reduces_remaining()
    {
        var balance = Balance();
        balance.Provision(1000, null, Now);
        balance.Add(250, Now);
        balance.Add(100, Now);
        Assert.Equal(350, balance.UsedQuantity);
        Assert.Equal(650, balance.RemainingQuantity);
        Assert.Equal(0, balance.OverageQuantity);
    }

    [Fact]
    public void Usage_beyond_included_becomes_overage()
    {
        var balance = Balance();
        balance.Provision(100, null, Now);
        balance.Add(150, Now);
        Assert.Equal(0, balance.RemainingQuantity);
        Assert.Equal(50, balance.OverageQuantity);
    }

    [Fact]
    public void Add_rejects_non_positive_quantity()
    {
        Assert.Throws<FulfillmentRuleException>(() => Balance().Add(0, Now));
        Assert.Throws<FulfillmentRuleException>(() => Balance().Provision(-1, null, Now));
    }
}
