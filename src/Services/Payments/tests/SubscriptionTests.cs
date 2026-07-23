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
    public void Start_records_the_first_period_in_history()
    {
        var sub = Start(BillingPeriod.Monthly);
        var first = Assert.Single(sub.Renewals);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(sub.CurrentPeriodStart, first.PeriodStart);
        Assert.Equal(sub.CurrentPeriodEnd, first.PeriodEnd);
        Assert.Equal(sub.PriceMinor, first.AmountMinor);
        Assert.Equal(sub.Currency, first.Currency);
        Assert.Equal(sub.Id, first.SubscriptionId);
    }

    [Fact]
    public void Renew_appends_a_history_row_with_the_next_sequence()
    {
        var sub = Start(BillingPeriod.Monthly);
        var openedEnd = sub.CurrentPeriodEnd;

        sub.Renew(openedEnd);

        Assert.Equal(2, sub.Renewals.Count);
        var second = sub.Renewals[1];
        Assert.Equal(2, second.Sequence);
        Assert.Equal(openedEnd, second.PeriodStart);
        Assert.Equal(openedEnd.AddMonths(1), second.PeriodEnd);

        sub.Renew(sub.CurrentPeriodEnd);
        Assert.Equal(new[] { 1, 2, 3 }, sub.Renewals.Select(r => r.Sequence).ToArray());
    }

    [Fact]
    public void Past_due_does_not_record_history()
    {
        var sub = Start(BillingPeriod.Monthly);
        sub.MarkPastDue(Now);
        // Only the first period exists; a failed charge that never advanced writes no history.
        Assert.Single(sub.Renewals);
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
