namespace ThreeCommerce.Payments.Domain;

/// <summary>
/// The dunning rules every unsettled recurring charge follows (owner decisions 2026-10-27, ADR-0057): a subscription
/// renewal (<see cref="Subscription"/>) and a metered usage charge (<see cref="UsageCharge"/>,
/// <c>usage_charge_failure_feedback</c>) are retried on days 1, 3 and 7 after their first failure; when the day-7 retry
/// does not settle the subscription is cancelled / the usage charge abandoned. One definition, so the two can never drift.
/// Pure: the caller holds <c>start</c> (the first failure) and <c>lastAttemptAt</c> (when the provider last ANSWERED an
/// attempt — an outage never uses up a retry), both at stored precision (<see cref="StoredTime"/>).
/// </summary>
public static class DunningSchedule
{
    /// <summary>Scheduled retries, in days after the first failure; the last one failing ends the dunning.</summary>
    public static readonly IReadOnlyList<int> RetryDays = [1, 3, 7];

    /// <summary>
    /// The scheduled retry due at <paramref name="now"/>, if any: retry k (1-based) falls due once
    /// <c>start + RetryDays[k-1]</c> has passed and nothing was attempted since then (a customer's own attempt counts).
    /// Several missed days collapse into one retry. <c>IsFinal</c>: the last one.
    /// </summary>
    public static (int Retry, bool IsFinal)? Due(DateTimeOffset start, DateTimeOffset? lastAttemptAt, DateTimeOffset now)
    {
        var due = RetryDays.Count(d => start.AddDays(d) <= now);
        if (due == 0 || lastAttemptAt >= start.AddDays(RetryDays[due - 1]))
        {
            return null;
        }

        return (due, due == RetryDays.Count);
    }

    /// <summary>When the next scheduled retry falls due (null when none is left).</summary>
    public static DateTimeOffset? NextRetryAt(DateTimeOffset start, DateTimeOffset? lastAttemptAt) =>
        RetryDays.Select(d => start.AddDays(d)).Cast<DateTimeOffset?>().FirstOrDefault(t => !(lastAttemptAt >= t));

    /// <summary>When an unpaid charge's dunning ends: the final scheduled retry.</summary>
    public static DateTimeOffset EndsAfter(DateTimeOffset start) => start.AddDays(RetryDays[^1]);

    /// <summary>The provider key of attempt <paramref name="attempt"/>: the charge's own key first, then a FRESH <c>{key}-a{n}</c>
    /// (a provider replays a reused key's result — Stripe for about 24 hours), while the amount stays pinned to <paramref name="key"/>.</summary>
    public static string AttemptKey(string key, int attempt) => attempt == 0 ? key : $"{key}-a{attempt}";
}
