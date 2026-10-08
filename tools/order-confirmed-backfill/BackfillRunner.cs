using System.Globalization;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill;

/// <summary>A computed plan plus the loaded aggregates the events are rebuilt from.</summary>
public sealed record BackfillRun(BackfillPlan Plan, IReadOnlyDictionary<Guid, Order> Orders, DateTimeOffset? DeliveryLogStart);

/// <summary>Loads the three services' records and plans the backfill. Reads only — sending is the caller's step.</summary>
public sealed class BackfillRunner(OrderSource orders, FulfillmentSource fulfillment, DeliverySource deliveries)
{
    public async Task<BackfillRun> PlanAsync(BackfillOptions options, CancellationToken ct)
    {
        var inScope = (Guid tenant) => options.Tenants.Count == 0 || options.Tenants.Contains(tenant);
        var wantsFulfillment = options.Targets.Contains(BackfillTarget.Fulfillment);
        var wantsNotifications = options.Targets.Contains(BackfillTarget.Notifications);

        // ALL tenants' orders are loaded even with --tenant: the delivery log is not tenant-scoped, and one address
        // can shop at two tenants — matching emails against a subset would let one tenant's order claim another's email.
        var all = new Dictionary<Guid, Order>();
        var seen = new HashSet<Guid>();
        foreach (var tenant in await orders.TenantsAsync(ct))
        {
            var tenantOrders = await orders.ConfirmedOrdersAsync(tenant, ct);
            foreach (var order in tenantOrders)
            {
                all[order.Id] = order;
            }

            if (wantsFulfillment && inScope(tenant) && tenantOrders.Count > 0)
            {
                seen.UnionWith(await fulfillment.SeenAsync(tenant, tenantOrders.Select(o => o.Id).ToList(), ct));
            }
        }

        var facts = all.Values.Select(OrderFacts.From).ToList();

        EmailEvidenceResult email = new(new Dictionary<Guid, EmailEvidence>(), 0, []);
        DateTimeOffset? logStart = null;
        if (wantsNotifications)
        {
            if (!await deliveries.HasReferenceColumnAsync(ct))
            {
                throw new BackfillPreconditionException(
                    "notifications.deliveries has no Reference column: start the Notifications worker from a build with the " +
                    "DeliveryReference migration first (it migrates on start). Without it a re-run could re-email everyone it emailed.");
            }

            logStart = await deliveries.LogStartAsync(ct);
            email = EmailEvidenceMatcher.Classify(facts, await deliveries.OrderConfirmationDeliveriesAsync(ct), logStart, options.EmailMatch);
        }

        var plan = BackfillPlanner.Plan(
            facts.Where(f => inScope(f.TenantId)).ToList(),
            seen,
            email,
            options.IncludeUnprovenEmail,
            options.Limit);
        return new BackfillRun(plan, all, logStart);
    }

    /// <summary>The human report. Order ids, numbers and tenants only — never an email address.</summary>
    public static void WriteReport(BackfillRun run, BackfillOptions options, TextWriter output)
    {
        var p = run.Plan;
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(inv, $"confirmed-family orders in scope : {p.ConfirmedOrders}"));
        foreach (var (reason, count) in p.Skipped.OrderBy(kv => kv.Key))
        {
            output.WriteLine(string.Create(inv, $"  skipped ({reason,-9})           : {count}"));
        }

        if (options.Targets.Contains(BackfillTarget.Fulfillment))
        {
            output.WriteLine();
            output.WriteLine(string.Create(inv, $"[fulfillment] → queue:{BackfillQueues.QueueName(BackfillTarget.Fulfillment)}"));
            output.WriteLine(string.Create(inv, $"  eligible with a shippable line : {p.ShippableEligible}"));
            output.WriteLine(string.Create(inv, $"  already shipped or held        : {p.FulfillmentAlreadySeen}"));
            output.WriteLine(string.Create(inv, $"  MISSING (to send)              : {p.FulfillmentMissing}{Capped(p.Fulfillment.Count, p.FulfillmentMissing)}"));
            List(p.Fulfillment, null);
        }

        if (options.Targets.Contains(BackfillTarget.Notifications))
        {
            output.WriteLine();
            output.WriteLine(string.Create(inv, $"[notifications] → queue:{BackfillQueues.QueueName(BackfillTarget.Notifications)}"));
            output.WriteLine(string.Create(inv, $"  delivery log starts            : {run.DeliveryLogStart?.ToString("u", inv) ?? "(empty)"}"));
            output.WriteLine(string.Create(inv, $"  match window / skew / orphan   : {options.EmailMatch.Window.TotalSeconds}s / {options.EmailMatch.Skew.TotalSeconds}s / {options.EmailMatch.OrphanReach.TotalMinutes}min"));
            foreach (var evidence in Enum.GetValues<EmailEvidence>())
            {
                output.WriteLine(string.Create(inv, $"  {evidence,-30} : {p.EmailEvidenceCounts.GetValueOrDefault(evidence)}"));
            }

            output.WriteLine(string.Create(inv, $"  orphan deliveries (unexplained): {p.Email.OrphanDeliveries}"));
            if (p.Email.MatchedLags.Count > 0)
            {
                var lags = p.Email.MatchedLags.Select(l => l.TotalMilliseconds).Order().ToList();
                output.WriteLine(string.Create(inv, $"  time-matched lag ms p50/p95/max: {Pct(lags, 0.50):F0} / {Pct(lags, 0.95):F0} / {lags[^1]:F0}"));
            }

            output.WriteLine(string.Create(inv, $"  SELECTED (to send)             : {p.NotificationsSelected}{Capped(p.Notifications.Count, p.NotificationsSelected)}")
                + (options.IncludeUnprovenEmail ? "  (includes Ambiguous + PredatesDeliveryLog)" : "  (Missing only)"));
            List(p.Notifications, p.Email);
        }

        void List(IReadOnlyList<OrderFacts> selected, EmailEvidenceResult? evidence)
        {
            if (!options.List)
            {
                return;
            }

            foreach (var o in selected)
            {
                var tag = evidence is null ? string.Empty : $"  {evidence.ByOrder[o.OrderId]}";
                output.WriteLine(string.Create(inv, $"    {o.OrderId}  #{o.PublicOrderNumber}  tenant={o.TenantId}  confirmed={o.ConfirmedAt:u}{tag}"));
            }
        }

        static string Capped(int sending, int total) =>
            sending < total ? string.Create(CultureInfo.InvariantCulture, $"  (sending {sending} — --limit)") : string.Empty;
    }

    private static double Pct(List<double> sorted, double q) => sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(q * sorted.Count))];
}

public sealed class BackfillPreconditionException(string message) : Exception(message);
