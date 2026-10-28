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
    public void Start_captures_the_renewal_instrument_for_off_session_charges()
    {
        var sub = Subscription.Start(
            Guid.NewGuid(), Guid.NewGuid(), "buyer@example.com", Guid.NewGuid(), null,
            BillingPeriod.Monthly, 1500, "EUR", Now, providerCustomerId: "cus_1", providerPaymentMethodId: "pm_1");

        Assert.Equal("cus_1", sub.ProviderCustomerId);
        Assert.Equal("pm_1", sub.ProviderPaymentMethodId);
    }

    [Fact]
    public void Start_without_an_instrument_leaves_the_renewal_refs_null()
    {
        var sub = Start(BillingPeriod.Monthly);

        Assert.Null(sub.ProviderCustomerId);
        Assert.Null(sub.ProviderPaymentMethodId);
    }

    [Fact]
    public void Start_captures_the_storefront_for_per_storefront_auto_renew()
    {
        var storefront = Guid.NewGuid();
        var sub = Subscription.Start(
            Guid.NewGuid(), Guid.NewGuid(), "buyer@example.com", Guid.NewGuid(), null,
            BillingPeriod.Monthly, 1500, "EUR", Now, storefrontId: storefront);

        Assert.Equal(storefront, sub.StorefrontId);
    }

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

    [Fact]
    public void A_legacy_single_unit_subscription_is_corrected_once_to_its_line_quantity_and_price()
    {
        // subscription_quantity_backfill (ADR-0057 amendment 2026-10-27): stored before quantity was carried.
        var sub = Start(price: 500);

        Assert.True(sub.CorrectLegacyQuantity(3, 4500, Now.AddDays(1)));
        Assert.Equal(3, sub.Quantity);
        Assert.Equal(4500, sub.PriceMinor);
        Assert.Equal(Now.AddDays(1), sub.UpdatedAt);
        Assert.False(sub.CorrectLegacyQuantity(3, 4500, Now.AddDays(2)));   // a repeat is a no-op
        Assert.False(sub.CorrectLegacyQuantity(5, 9999, Now.AddDays(2)));   // a carried quantity is never re-priced
        Assert.Equal(4500, sub.PriceMinor);
    }

    [Fact]
    public void A_single_unit_request_never_reprices_a_subscription()
    {
        var sub = Start(price: 1500);

        Assert.False(sub.CorrectLegacyQuantity(1, 9999, Now));
        Assert.False(sub.CorrectLegacyQuantity(0, 9999, Now));
        Assert.Equal(1500, sub.PriceMinor);
        Assert.Equal(1, sub.Quantity);
    }
}
