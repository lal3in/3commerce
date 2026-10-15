using System.Diagnostics;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane (no Docker): the shared projection wait (<see cref="Eventually"/>) looks once more after its budget,
/// so a condition that became true while the test was not polling (the last delay, a host sleep) is not reported
/// as a timeout — the gap behind the 2026-10-15 CheckoutOfferGateParityTests failure.
/// </summary>
public class EventuallyTests
{
    [Fact]
    public async Task A_condition_that_holds_only_after_the_budget_is_seen_by_the_final_poll()
    {
        var budget = TimeSpan.FromMilliseconds(150);
        var clock = Stopwatch.StartNew();
        var polls = 0;

        // Stands in for a projection that lands during the last delay (or while the host slept): false on every
        // poll inside the budget, true afterwards.
        var held = await Eventually.PollAsync(() =>
        {
            polls++;
            return Task.FromResult(clock.Elapsed >= budget);
        }, budget, interval: TimeSpan.FromMilliseconds(400));

        Assert.True(held);
        Assert.Equal(2, polls); // one inside the budget, then the final poll after it
    }

    [Fact]
    public async Task A_condition_that_holds_returns_without_waiting_out_the_budget()
    {
        var clock = Stopwatch.StartNew();
        Assert.True(await Eventually.PollAsync(() => Task.FromResult(true), TimeSpan.FromSeconds(30)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_condition_that_never_holds_throws_the_callers_exception_after_the_final_poll()
    {
        var polls = 0;
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Eventually.UntilAsync(
            () => { polls++; return Task.FromResult(false); },
            () => new TimeoutException($"still false after {polls} polls"),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(20)));

        Assert.True(polls >= 2);
        Assert.Equal($"still false after {polls} polls", ex.Message);
    }
}
