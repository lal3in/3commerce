using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Usage.Domain;

namespace ThreeCommerce.Usage.Tests;

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
        balance.Provision(1000, false, 0, "AUD", null, Now);
        Assert.Equal(1000, balance.IncludedQuantity);
        Assert.Equal(1000, balance.RemainingQuantity);
    }

    [Fact]
    public void Add_increments_used_and_reduces_remaining()
    {
        var balance = Balance();
        balance.Provision(1000, false, 0, "AUD", null, Now);
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
        balance.Provision(100, true, 5, "AUD", null, Now);
        balance.Add(150, Now);
        Assert.Equal(0, balance.RemainingQuantity);
        Assert.Equal(50, balance.OverageQuantity);
    }

    [Fact]
    public void Add_rejects_non_positive_quantity()
    {
        Assert.Throws<UsageRuleException>(() => Balance().Add(0, Now));
        Assert.Throws<UsageRuleException>(() => Balance().Provision(-1, false, 0, "AUD", null, Now));
    }

    // ---- mt7_5: access gate + overage billing ----

    [Fact]
    public void Access_is_gated_when_overage_is_not_allowed()
    {
        var balance = Balance();
        balance.Provision(100, overageAllowed: false, 0, "AUD", null, Now);
        Assert.True(balance.CanAccept(100));
        Assert.False(balance.CanAccept(101));
    }

    [Fact]
    public void Overage_is_always_acceptable_when_allowed()
    {
        var balance = Balance();
        balance.Provision(100, overageAllowed: true, 5, "AUD", null, Now);
        Assert.True(balance.CanAccept(1_000_000));
    }

    [Fact]
    public void Unbilled_overage_charge_rates_quantity_and_clears_after_billing()
    {
        var balance = Balance();
        balance.Provision(100, true, overageUnitPriceMinor: 5, "AUD", null, Now);
        balance.Add(130, Now); // 30 over

        Assert.Equal(30, balance.UnbilledOverageQuantity);
        Assert.Equal(150, balance.UnbilledOverageChargeMinor); // 30 × 5

        balance.MarkOverageBilled(Now);
        Assert.Equal(0, balance.UnbilledOverageQuantity);
        Assert.Equal(0, balance.UnbilledOverageChargeMinor);

        balance.Add(20, Now); // 20 more over → only the new overage is unbilled
        Assert.Equal(20, balance.UnbilledOverageQuantity);
        Assert.Equal(100, balance.UnbilledOverageChargeMinor);
    }

    // ---- mt7_5: period close / roll ----

    [Fact]
    public void Is_due_for_close_once_the_window_end_has_passed()
    {
        var balance = Balance();
        balance.Provision(100, true, 5, "AUD", periodEnd: Now.AddDays(30), Now);

        Assert.False(balance.IsDueForClose(Now.AddDays(29)));
        Assert.True(balance.IsDueForClose(Now.AddDays(30)));
        Assert.True(balance.IsDueForClose(Now.AddDays(31)));
    }

    [Fact]
    public void Rolling_a_bounded_period_resets_counters_and_advances_the_window_by_its_length()
    {
        var balance = Balance();
        balance.Provision(100, true, 5, "AUD", periodEnd: Now.AddDays(30), Now);
        balance.Add(130, Now);          // 30 over
        balance.MarkOverageBilled(Now); // billed before rolling

        balance.RollToNextPeriod(Now.AddDays(30));

        Assert.Equal(0, balance.UsedQuantity);
        Assert.Equal(0, balance.OverageQuantity);
        Assert.Equal(0, balance.UnbilledOverageQuantity);
        Assert.Equal(Now.AddDays(30), balance.PeriodStart);   // new window starts at the old end
        Assert.Equal(Now.AddDays(60), balance.PeriodEnd);      // advanced by the 30-day length
        Assert.Equal(100, balance.IncludedQuantity);           // allowance carries over
    }

    [Fact]
    public void An_open_ended_balance_rolls_by_resetting_counters_only()
    {
        var balance = Balance();
        balance.Provision(100, true, 5, "AUD", periodEnd: null, Now);
        balance.Add(120, Now);

        balance.RollToNextPeriod(Now.AddDays(1));

        Assert.Equal(0, balance.UsedQuantity);
        Assert.Null(balance.PeriodEnd);
        Assert.Equal(Now.AddDays(1), balance.PeriodStart);
    }
}
