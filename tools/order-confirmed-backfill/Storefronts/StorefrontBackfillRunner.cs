using System.Globalization;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Storefronts;

/// <summary>Loads Catalog, the audit timeline, Payments and Fulfillment, and plans. Reads only — sending is the caller's step.</summary>
public sealed class StorefrontBackfillRunner(
    DuplicationAuditSource audit,
    StorefrontCatalogSource catalog,
    PaymentAccountSource payments,
    CarrierIntegrationSource carriers)
{
    public async Task<StorefrontBackfillPlan> PlanAsync(StorefrontBackfillOptions options, CancellationToken ct)
    {
        var inScope = (Guid tenant) => options.Tenants.Count == 0 || options.Tenants.Contains(tenant);
        var audited = (await audit.DuplicationsAsync(ct)).ToLookup(d => d.TenantId);

        var verdicts = new List<DuplicationVerdict>();
        foreach (var tenant in (await catalog.TenantsAsync(ct)).Concat(audited.Select(g => g.Key)).Distinct().Where(inScope).Order())
        {
            var storefronts = await catalog.StorefrontsAsync(tenant, ct);
            var publications = await catalog.PublicationsAsync(tenant, ct);
            var duplications = audited[tenant]
                .Concat(StorefrontBackfillPlanner.ProvenByLink(tenant, storefronts))
                .Concat(StorefrontBackfillPlanner.ProvenByPublications(tenant, storefronts, publications))
                .ToList();
            if (duplications.Count == 0)
            {
                continue;
            }

            var snapshot = new StorefrontTenantSnapshot(
                tenant, storefronts, duplications, publications,
                await payments.RowsAsync(tenant, ct),
                await carriers.RowsAsync(tenant, ct));
            verdicts.AddRange(StorefrontBackfillPlanner.Plan(snapshot, options.CloneWindow));
        }

        return StorefrontBackfillPlan.From(verdicts, options.Limit);
    }

    /// <summary>The human report: storefront ids, names and tenants — no secrets, no credential references.</summary>
    public static void WriteReport(StorefrontBackfillPlan plan, StorefrontBackfillOptions options, TextWriter output)
    {
        var inv = CultureInfo.InvariantCulture;
        var v = plan.Verdicts;
        bool Is(DuplicationVerdict d, StorefrontBackfillTarget t, SideOutcome o) => d.Side(t).Outcome == o;
        const StorefrontBackfillTarget pay = StorefrontBackfillTarget.Payments;
        const StorefrontBackfillTarget ful = StorefrontBackfillTarget.Fulfillment;

        var skipped = v.Count(d => Is(d, pay, SideOutcome.Skipped));
        var undetermined = v.Count(d => Is(d, pay, SideOutcome.Undetermined) || Is(d, ful, SideOutcome.Undetermined));
        var missingPay = v.Count(d => Is(d, pay, SideOutcome.Missing));
        var missingFul = v.Count(d => Is(d, ful, SideOutcome.Missing));
        var missingBoth = v.Count(d => Is(d, pay, SideOutcome.Missing) && Is(d, ful, SideOutcome.Missing));
        var complete = v.Count(d => !Is(d, pay, SideOutcome.Skipped)
            && d.Payments.Outcome is SideOutcome.Present or SideOutcome.SourceEmpty
            && d.Fulfillment.Outcome is SideOutcome.Present or SideOutcome.SourceEmpty);

        output.WriteLine(string.Create(inv, $"duplicated storefronts in scope    : {v.Count}"));
        output.WriteLine(string.Create(inv, $"  complete (nothing missing)       : {complete}"));
        output.WriteLine(string.Create(inv, $"  missing payment accounts         : {missingPay}"));
        output.WriteLine(string.Create(inv, $"  missing carrier integrations     : {missingFul}"));
        output.WriteLine(string.Create(inv, $"  missing both                     : {missingBoth}"));
        output.WriteLine(string.Create(inv, $"  cannot be determined (any side)  : {undetermined}"));
        output.WriteLine(string.Create(inv, $"  skipped (archived / not found)   : {skipped}"));

        foreach (var target in options.Targets)
        {
            output.WriteLine();
            output.WriteLine(string.Create(inv, $"[{target.ToString().ToLowerInvariant()}] → queue:{StorefrontBackfillQueues.QueueName(target)}"));
            output.WriteLine(string.Create(inv, $"  present on the duplicate         : {v.Count(d => Is(d, target, SideOutcome.Present))}"));
            output.WriteLine(string.Create(inv, $"  source had nothing to copy       : {v.Count(d => Is(d, target, SideOutcome.SourceEmpty))}"));
            foreach (var reason in Enum.GetValues<UndeterminedReason>().Where(r => r != UndeterminedReason.None))
            {
                output.WriteLine(string.Create(inv, $"  undetermined ({reason,-18}) : {v.Count(d => Is(d, target, SideOutcome.Undetermined) && d.Side(target).Reason == reason)}"));
            }

            var selected = plan.Selected(target).Count;
            var missing = plan.MissingCount(target);
            output.WriteLine(string.Create(inv, $"  MISSING (to send)                : {missing}")
                + (selected < missing ? string.Create(inv, $"  (sending {selected} — --limit)") : string.Empty));
        }

        if (!options.List)
        {
            return;
        }

        output.WriteLine();
        foreach (var d in v.OrderBy(d => d.TenantId).ThenBy(d => d.CreatedAt))
        {
            output.WriteLine(string.Create(inv,
                $"  {d.TargetId}  tenant={d.TenantId}  created={d.CreatedAt:u}  \"{d.Name}\"  evidence={d.Evidence}  candidates={d.Candidates}"));
            output.WriteLine(string.Create(inv, $"      payments: {d.Payments}   fulfillment: {d.Fulfillment}"));
        }
    }
}
