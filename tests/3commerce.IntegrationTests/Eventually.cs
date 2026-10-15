using System.Diagnostics;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Polls an eventually-consistent condition (a projection landing, a consumer finishing) for a bounded time.
/// <para>
/// Two things a hand-rolled <c>while (DateTimeOffset.UtcNow &lt; deadline)</c> loop gets wrong:
/// <list type="number">
/// <item>The deadline is wall-clock time. When the Mac running the suite sleeps (a closed lid, or a dark-wake
/// maintenance window ending), the test process, Docker VM and broker all pause, but the wall clock does not.
/// On wake the deadline has long passed and the test fails after one second of awake time (xUnit shows
/// <c>[1 s]</c>) with the message not yet consumed. This budget is <see cref="Stopwatch"/> time, which on macOS
/// (CLOCK_UPTIME_RAW) and Linux (CLOCK_MONOTONIC) stops while the host is asleep.</item>
/// <item>The loop exits on the deadline without looking again, so a condition that became true while the test
/// was not running (the delay, or a sleep) is reported as a timeout. This polls once more after the budget.</item>
/// </list>
/// </para>
/// </summary>
public static class Eventually
{
    /// <summary>The usual budget for a cross-service projection.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Returns true as soon as <paramref name="condition"/> holds; false if it still does not after
    /// <paramref name="timeout"/> of awake time and one final poll.
    /// </summary>
    public static async Task<bool> PollAsync(Func<Task<bool>> condition, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var budget = timeout ?? DefaultTimeout;
        var step = interval ?? DefaultInterval;
        var awake = Stopwatch.StartNew();
        while (awake.Elapsed < budget)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(step);
        }

        return await condition();
    }

    /// <summary>
    /// Waits for <paramref name="condition"/>; when it never holds, throws the exception <paramref name="timedOut"/>
    /// builds, which can describe the last state the test saw.
    /// </summary>
    public static async Task UntilAsync(
        Func<Task<bool>> condition, Func<Exception> timedOut, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        if (!await PollAsync(condition, timeout, interval))
        {
            throw timedOut();
        }
    }
}
