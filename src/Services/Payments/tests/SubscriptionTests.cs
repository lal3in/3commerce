using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Tests;

public class SubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);

    private static Subscription Start(BillingPeriod period = BillingPeriod.Monthly, long price = 1500) =>
        Subscription.Start(Guid.NewGuid(), Guid.NewGuid(), "BUYER@Example.com", Guid.NewGuid(), null, period, price, "eur", Now);

    [Fact]
    public void Start_opens_an_active_first_period()
    {
        var sub = Start(BillingPeriod.Monthly);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal(Now, sub.CurrentPeriodStart);
        Assert.Equal(Now.AddMonths(1), sub.CurrentPeriodEnd);
        Assert.Equal("buyer@example.com", sub.CustomerEmail);
        Assert.Equal("EUR", sub.Currency);
    }

    [Fact]
    public void Start_requires_a_recurring_period() =>
        Assert.Throws<SubscriptionRuleException>(() => Start(BillingPeriod.Once));

    [Fact]
    public void Renew_advances_the_period()
    {
        var sub = Start(BillingPeriod.Monthly);
        var previousEnd = sub.CurrentPeriodEnd;
        sub.Renew(Now.AddMonths(1));
        Assert.Equal(previousEnd, sub.CurrentPeriodStart);
        Assert.Equal(previousEnd.AddMonths(1), sub.CurrentPeriodEnd);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public void Past_due_then_renew_recovers()
    {
        var sub = Start();
        sub.MarkPastDue(Now);
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
        sub.Renew(Now);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public void Cancelled_subscription_cannot_renew()
    {
        var sub = Start();
        sub.Cancel(Now);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        Assert.Throws<SubscriptionRuleException>(() => sub.Renew(Now));
    }

    [Fact]
    public void Yearly_period_advances_by_a_year()
    {
        var sub = Start(BillingPeriod.Yearly);
        Assert.Equal(Now.AddYears(1), sub.CurrentPeriodEnd);
    }
}
