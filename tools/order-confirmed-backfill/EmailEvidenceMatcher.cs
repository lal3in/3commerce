using ThreeCommerce.Workers.Notifications.Domain;
using ThreeCommerce.Workers.Notifications.Email;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill;

/// <summary>How a legacy (reference-less) delivery may be tied to an order by time.</summary>
/// <param name="Window">How long after confirmation the email is normally sent (outbox + broker + worker lag). Keep it
/// close to the real lag: a wide window makes back-to-back orders of one address Ambiguous.</param>
/// <param name="Skew">How much EARLIER than the order's timestamp a delivery may be stamped (clock skew).</param>
/// <param name="OrphanReach">How far back an unexplained delivery may have been for: Missing orders confirmed up to
/// this long before an orphan delivery become Ambiguous (it may have been theirs, sent late).</param>
public sealed record EmailMatchOptions(TimeSpan Window, TimeSpan Skew, TimeSpan OrphanReach)
{
    /// <summary>
    /// 1 s window / 1 s skew: on the dev data (2026-10-08) the certain matches lagged p50 16 ms, p95 27 ms, max
    /// 299 ms behind confirmation, and seeded orders of one address land under 2 s apart — a 10 s window left 605
    /// orders Ambiguous, 1 s left 3. A slower worker shows up as orphan deliveries, which re-widen locally.
    /// </summary>
    public static EmailMatchOptions Default { get; } =
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(30));
}

public sealed record EmailEvidenceResult(
    IReadOnlyDictionary<Guid, EmailEvidence> ByOrder,
    int OrphanDeliveries,
    IReadOnlyList<TimeSpan> MatchedLags);

/// <summary>
/// Decides, per order, what the Notifications delivery log proves about its confirmation email.
/// <para>
/// Deliveries written since <c>DeliveryReference</c> carry <c>order-confirmed:{orderId}</c> — exact. Older rows only
/// say recipient + subject + time, so they are tied to orders by time: delivery <c>d</c> can be order <c>o</c>'s when
/// both go to the same address (case-insensitive) and <c>o.ConfirmedAt − Skew ≤ d.OccurredAt ≤ o.ConfirmedAt +
/// Window</c>. Per recipient, the matcher propagates only CERTAIN conclusions until nothing changes: a delivery
/// with exactly one open candidate order is that order's (EmailedByTimeMatch); an open order with no open candidate
/// delivery never got one (Missing). Whatever is left is Ambiguous — k emails for n &gt; k orders, unknown which.
/// </para>
/// <para>
/// Conservative on purpose — a wrong "Missing" means a customer gets a SECOND email: a delivery left with no candidate
/// ("orphan": sent later than the window, so the lag assumption failed there) turns every Missing order of that
/// recipient confirmed within <see cref="EmailMatchOptions.OrphanReach"/> before it into Ambiguous, and an order confirmed before the log's first row is
/// PredatesDeliveryLog unless a delivery provably matched it. Only Missing is sent by default.
/// </para>
/// </summary>
public static class EmailEvidenceMatcher
{
    public static EmailEvidenceResult Classify(
        IReadOnlyCollection<OrderFacts> orders,
        IReadOnlyCollection<DeliveryFact> deliveries,
        DateTimeOffset? deliveryLogStart,
        EmailMatchOptions options)
    {
        var result = new Dictionary<Guid, EmailEvidence>();
        var sent = deliveries.Where(d => d.Status == NotificationStatus.Sent).ToList();

        // 1. Exact: the delivery names its order.
        var referenced = new HashSet<Guid>();
        var legacy = new List<DeliveryFact>();
        foreach (var d in sent)
        {
            if (TryParseOrderReference(d.Reference, out var orderId))
            {
                referenced.Add(orderId);
            }
            else
            {
                legacy.Add(d);
            }
        }

        foreach (var o in orders.Where(o => referenced.Contains(o.OrderId)))
        {
            result[o.OrderId] = EmailEvidence.EmailedByReference;
        }

        // 2. Legacy rows, per recipient, by time.
        var lags = new List<TimeSpan>();
        var orphans = 0;
        var legacyByRecipient = legacy.ToLookup(d => Normalize(d.Recipient));
        var processed = new HashSet<string>();
        foreach (var group in orders.Where(o => !result.ContainsKey(o.OrderId)).GroupBy(o => Normalize(o.Email)))
        {
            processed.Add(group.Key);
            var (matched, missing, orphanTimes) = MatchRecipient(group.ToList(), legacyByRecipient[group.Key].ToList(), options, lags);
            orphans += orphanTimes.Count;
            foreach (var o in group)
            {
                result[o.OrderId] = matched.Contains(o.OrderId) ? EmailEvidence.EmailedByTimeMatch
                    : missing.Contains(o.OrderId) && !orphanTimes.Any(t => MayBeLateFor(o, t, options)) ? EmailEvidence.Missing
                    : EmailEvidence.Ambiguous;
            }
        }

        // Legacy deliveries to an address with no open order (none confirmed, or all referenced) explain nothing.
        orphans += legacy.Count(d => !processed.Contains(Normalize(d.Recipient)));

        // 3. No log yet when the order confirmed → no evidence, unless a delivery provably matched it.
        foreach (var o in orders)
        {
            var evidence = result[o.OrderId];
            if (evidence is EmailEvidence.Missing or EmailEvidence.Ambiguous
                && (deliveryLogStart is null || o.ConfirmedAt < deliveryLogStart.Value))
            {
                result[o.OrderId] = EmailEvidence.PredatesDeliveryLog;
            }
        }

        return new EmailEvidenceResult(result, orphans, lags);
    }

    public static bool TryParseOrderReference(string? reference, out Guid orderId)
    {
        orderId = Guid.Empty;
        return reference is not null
            && reference.StartsWith(NotificationReferences.OrderConfirmedPrefix, StringComparison.Ordinal)
            && Guid.TryParse(reference.AsSpan(NotificationReferences.OrderConfirmedPrefix.Length), out orderId);
    }

    private static bool MayBeLateFor(OrderFacts o, DateTimeOffset orphanAt, EmailMatchOptions options) =>
        orphanAt >= o.ConfirmedAt - options.Skew && orphanAt <= o.ConfirmedAt + options.OrphanReach;

    private static (HashSet<Guid> Matched, HashSet<Guid> Missing, List<DateTimeOffset> OrphanTimes) MatchRecipient(
        List<OrderFacts> orders, List<DeliveryFact> deliveries, EmailMatchOptions options, List<TimeSpan> lags)
    {
        bool InWindow(OrderFacts o, DeliveryFact d) =>
            d.OccurredAt >= o.ConfirmedAt - options.Skew && d.OccurredAt <= o.ConfirmedAt + options.Window;

        var matched = new HashSet<Guid>();
        var missing = new HashSet<Guid>();
        var consumed = new bool[deliveries.Count];
        var orphanTimes = new List<DateTimeOffset>();
        bool Open(OrderFacts o) => !matched.Contains(o.OrderId) && !missing.Contains(o.OrderId);

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < deliveries.Count; i++)
            {
                if (consumed[i])
                {
                    continue;
                }

                var candidates = orders.Where(o => Open(o) && InWindow(o, deliveries[i])).Take(2).ToList();
                if (candidates.Count == 0)
                {
                    // Every order this email could have been for is already accounted for (or none was in the
                    // window): the time model does not explain it, so this address's "Missing" is not provable.
                    consumed[i] = true;
                    orphanTimes.Add(deliveries[i].OccurredAt);
                    changed = true;
                }
                else if (candidates.Count == 1)
                {
                    consumed[i] = true;
                    matched.Add(candidates[0].OrderId);
                    lags.Add(deliveries[i].OccurredAt - candidates[0].ConfirmedAt);
                    changed = true;
                }
            }

            foreach (var o in orders.Where(Open))
            {
                var anyCandidate = false;
                for (var i = 0; i < deliveries.Count && !anyCandidate; i++)
                {
                    anyCandidate = !consumed[i] && InWindow(o, deliveries[i]);
                }

                if (!anyCandidate)
                {
                    missing.Add(o.OrderId);
                    changed = true;
                }
            }
        }

        return (matched, missing, orphanTimes);
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
