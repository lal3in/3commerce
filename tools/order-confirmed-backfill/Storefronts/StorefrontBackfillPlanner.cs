namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Storefronts;

/// <summary>
/// Decides, for every duplicated storefront of one tenant, whether its payment-account copy and its carrier copy
/// happened — and when one is missing, which storefront to copy it from. Pure: no I/O.
///
/// <para><b>The recorded link, when there is one.</b> Catalog records the source of every copy its duplicate endpoint
/// makes (<c>Storefront.DuplicatedFromStorefrontId</c>). A duplicate carrying it has exactly that one candidate —
/// evidence <c>link</c> — and none of the inference below applies (the agreement rule then reduces to "did the source
/// hold rows when the copy was made"). A link naming a storefront the tenant does not have is
/// <see cref="UndeterminedReason.NoCandidate"/>, never a guess.</para>
/// <para><b>Copies made before the link existed have it null</b> (they are not backfilled — Catalog cannot know their
/// source either; the <c>catalog.storefront.duplicate</c> audit entry carries only the clone's id and name). For them
/// the source is established from evidence the duplication left behind, conservatively:</para>
/// <list type="number">
/// <item><b>Lineage.</b> A publication's <c>PublishedAt</c> is set once and copied verbatim; the clone's publications
/// are created at the clone's own <c>CreatedAt</c>. So the source's (product, published-at) pairs as of that instant
/// EQUAL the clone's copied pairs. A candidate must be an older storefront of the same tenant with exactly that set.
/// With no published publication to go by, only a duplicate named "<c>X (copy)</c>" (the admin default) whose
/// unique older storefront <c>X</c> had none either is linked.</item>
/// <item><b>The half that did copy.</b> When the duplicate holds rows created within the clone window of its creation
/// (the copy that DID run), a candidate must have held the same providers / carriers just before.</item>
/// <item><b>Name.</b> Among several candidates, a duplicate named "<c>X (copy)</c>" narrows to the one called X.</item>
/// <item><b>Agreement.</b> A missing side is sent only when EVERY remaining candidate had rows on that side when the
/// duplicate was made AND all of them would copy the same rows — so even when the true source is one of several
/// siblings, what gets copied is the same. If none had rows, nothing was lost (<see cref="SideOutcome.SourceEmpty"/>);
/// anything else is <see cref="SideOutcome.Undetermined"/> and reported, never sent.</item>
/// </list>
/// <para>Duplicates are decided oldest first, and a sibling that is itself about to be backfilled — or was, by an
/// earlier (canary) run, i.e. holds exactly what the agreeing sources hold — counts as holding it since it was made.
/// So a chain of split copies of one source resolves in one run, and a partial run never stalls the rest.</para>
/// <para>A side on which the duplicate holds ANY row is <see cref="SideOutcome.Present"/> and never sent: nothing the
/// owner configured is overwritten (and both consumers no-op on a target that has rows anyway).</para>
/// </summary>
public static class StorefrontBackfillPlanner
{
    public const string CopySuffix = " (copy)";

    /// <summary>The default allowance between a duplicate's creation and its copied config rows (the consumer lag).</summary>
    public static readonly TimeSpan DefaultCloneWindow = TimeSpan.FromMinutes(10);

    /// <summary>The clone's publications are created in the same transaction, with the same timestamp, as the clone.</summary>
    private static readonly TimeSpan PublicationSlack = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Duplicates Catalog itself proves, audit entry or not: a storefront holding a publication first published BEFORE
    /// the storefront existed can only have got it copied from another storefront.
    /// </summary>
    public static IEnumerable<DuplicationFact> ProvenByPublications(
        Guid tenantId, IReadOnlyList<StorefrontFact> storefronts, IReadOnlyList<PublicationFact> publications)
    {
        var byStorefront = publications.ToLookup(p => p.StorefrontId);
        return storefronts
            .Where(s => byStorefront[s.Id].Any(p => p.ProductPublishedAt < s.CreatedAt))
            .Select(s => new DuplicationFact(tenantId, s.Id, null));
    }

    /// <summary>Duplicates Catalog recorded as such: every storefront carrying a link to the storefront it was copied from.</summary>
    public static IEnumerable<DuplicationFact> ProvenByLink(Guid tenantId, IReadOnlyList<StorefrontFact> storefronts) =>
        storefronts
            .Where(s => s.DuplicatedFromStorefrontId is not null)
            .Select(s => new DuplicationFact(tenantId, s.Id, null));

    public static IReadOnlyList<DuplicationVerdict> Plan(StorefrontTenantSnapshot snapshot, TimeSpan cloneWindow)
    {
        var storefronts = snapshot.Storefronts.ToDictionary(s => s.Id);
        var publications = snapshot.Publications.ToLookup(p => p.StorefrontId);
        var rows = new Dictionary<StorefrontBackfillTarget, ILookup<Guid, ConfigRowFact>>
        {
            [StorefrontBackfillTarget.Payments] = snapshot.PaymentAccounts.ToLookup(r => r.StorefrontId),
            [StorefrontBackfillTarget.Fulfillment] = snapshot.CarrierIntegrations.ToLookup(r => r.StorefrontId),
        };

        // A storefront this run is about to backfill holds, for the purpose of later duplicates, what it will receive.
        var planned = new Dictionary<(StorefrontBackfillTarget Side, Guid Storefront), Guid>();
        var verdicts = new List<DuplicationVerdict>();

        var duplications = snapshot.Duplications
            .GroupBy(d => d.TargetId)
            .Select(g => g.OrderBy(d => d.NameAtDuplication is null).First()) // prefer the audited one (it has the name)
            .ToList();

        foreach (var dup in duplications.Where(d => !storefronts.ContainsKey(d.TargetId)))
        {
            verdicts.Add(new DuplicationVerdict(snapshot.TenantId, dup.TargetId, dup.NameAtDuplication ?? "?", null,
                "target storefront not found", 0, SideVerdict.Skipped, SideVerdict.Skipped));
        }

        foreach (var dup in duplications.Where(d => storefronts.ContainsKey(d.TargetId))
                     .OrderBy(d => storefronts[d.TargetId].CreatedAt).ThenBy(d => d.TargetId))
        {
            verdicts.Add(Decide(dup, storefronts[dup.TargetId]));
        }

        return verdicts;

        DuplicationVerdict Decide(DuplicationFact dup, StorefrontFact target)
        {
            var name = dup.NameAtDuplication ?? target.Name;
            if (target.Archived)
            {
                return new DuplicationVerdict(snapshot.TenantId, target.Id, name, target.CreatedAt, "target archived", 0,
                    SideVerdict.Skipped, SideVerdict.Skipped);
            }

            var t0 = target.CreatedAt;
            var present = Sides.ToDictionary(side => side, side => rows[side][target.Id].Any());

            var copiedPairs = publications[target.Id]
                .Where(p => p.ProductPublishedAt < t0 && (p.CreatedAt - t0).Duration() <= PublicationSlack)
                .Select(p => (p.ProductId, p.ProductPublishedAt))
                .ToHashSet();
            var sourceName = dup.NameAtDuplication is { } n && n.EndsWith(CopySuffix, StringComparison.Ordinal) && n.Length > CopySuffix.Length
                ? n[..^CopySuffix.Length]
                : null;

            var older = snapshot.Storefronts.Where(s => s.Id != target.Id && s.CreatedAt < t0).ToList();
            List<StorefrontFact> candidates;
            var evidence = new List<string>();
            if (target.DuplicatedFromStorefrontId is { } linked)
            {
                // Catalog recorded the source: exactly that storefront, whatever the circumstantial evidence says.
                if (!storefronts.ContainsKey(linked) || linked == target.Id)
                {
                    return Unresolved(UndeterminedReason.NoCandidate, "link", 0);
                }

                return Resolve([storefronts[linked]], "link");
            }

            if (copiedPairs.Count > 0)
            {
                candidates = older.Where(c => PairsAt(c.Id, t0).SetEquals(copiedPairs)).ToList();
                evidence.Add("publications");
            }
            else if (sourceName is not null)
            {
                // Storefront names are unique per tenant, so this is at most one storefront.
                candidates = older.Where(c => string.Equals(c.Name, sourceName, StringComparison.Ordinal) && PairsAt(c.Id, t0).Count == 0).ToList();
                evidence.Add("name");
            }
            else
            {
                return Unresolved(UndeterminedReason.NoLineage, "none", 0);
            }

            // The half that DID copy: the source held the same providers / carriers just before the copy ran.
            foreach (var side in Sides)
            {
                var clones = rows[side][target.Id].Where(r => r.CreatedAt >= t0 && r.CreatedAt <= t0 + cloneWindow).ToList();
                if (clones.Count == 0)
                {
                    continue;
                }

                var firstClone = clones.Min(r => r.CreatedAt);
                var cloned = Multiset(clones.Select(r => r.MatchKey));
                candidates = candidates
                    .Where(c => Multiset(rows[side][c.Id].Where(r => r.CreatedAt < firstClone).Select(r => r.MatchKey)) == cloned)
                    .ToList();
                evidence.Add(side == StorefrontBackfillTarget.Payments ? "copied-payments" : "copied-carriers");
            }

            if (candidates.Count > 1 && sourceName is not null)
            {
                var named = candidates.Where(c => string.Equals(c.Name, sourceName, StringComparison.Ordinal)).ToList();
                if (named.Count == 1)
                {
                    candidates = named;
                    evidence.Add("name");
                }
            }

            var evidenceText = string.Join('+', evidence);
            if (candidates.Count == 0)
            {
                return Unresolved(UndeterminedReason.NoCandidate, evidenceText, 0);
            }

            return Resolve(candidates, evidenceText);

            // Decide each side of a duplicate whose possible sources are known (one when Catalog recorded the link).
            DuplicationVerdict Resolve(List<StorefrontFact> sources, string sourceEvidence)
            {
                var verdict = new Dictionary<StorefrontBackfillTarget, SideVerdict>();
                foreach (var side in Sides)
                {
                    if (!present[side])
                    {
                        verdict[side] = DecideMissingSide(side, sources, t0);
                        if (verdict[side] is { Outcome: SideOutcome.Missing, SourceStorefrontId: { } source })
                        {
                            planned[(side, target.Id)] = source;
                        }

                        continue;
                    }

                    verdict[side] = SideVerdict.Present;

                    // A side filled AFTER the copy window (by an earlier, e.g. canary, backfill run — or by hand) with exactly
                    // what the agreeing sources hold counts, for later duplicates, as held since creation. Otherwise a
                    // partial run would make a backfilled sibling look like a candidate that "had nothing" and stall the rest.
                    if (!rows[side][target.Id].Any(r => r.CreatedAt <= t0 + cloneWindow)
                        && DecideMissingSide(side, sources, t0) is { Outcome: SideOutcome.Missing, SourceStorefrontId: { } src }
                        && Multiset(rows[side][target.Id].Select(r => r.CopyKey)) == Multiset(rows[side][src].Select(r => r.CopyKey)))
                    {
                        planned[(side, target.Id)] = src;
                    }
                }

                return Verdict(sourceEvidence, sources.Count, verdict[StorefrontBackfillTarget.Payments], verdict[StorefrontBackfillTarget.Fulfillment]);
            }

            DuplicationVerdict Verdict(string ev, int count, SideVerdict payments, SideVerdict fulfillment) =>
                new(snapshot.TenantId, target.Id, name, target.CreatedAt, ev, count, payments, fulfillment);

            // A duplicate with rows on both sides has nothing to send, whatever the evidence says.
            DuplicationVerdict Unresolved(UndeterminedReason reason, string ev, int count) => Verdict(ev, count,
                present[StorefrontBackfillTarget.Payments] ? SideVerdict.Present : SideVerdict.Undetermined(reason),
                present[StorefrontBackfillTarget.Fulfillment] ? SideVerdict.Present : SideVerdict.Undetermined(reason));
        }

        SideVerdict DecideMissingSide(StorefrontBackfillTarget side, List<StorefrontFact> candidates, DateTimeOffset t0)
        {
            var states = candidates.Select(c =>
            {
                var actual = rows[side][c.Id].ToList();
                if (actual.Any(r => r.CreatedAt < t0))
                {
                    return (Had: true, Copy: Multiset(actual.Select(r => r.CopyKey)), Provider: c);
                }

                if (planned.TryGetValue((side, c.Id), out var provider))
                {
                    return (Had: true, Copy: Multiset(rows[side][provider].Select(r => r.CopyKey)), Provider: storefronts[provider]);
                }

                return (Had: false, Copy: string.Empty, Provider: c);
            }).ToList();

            if (states.All(s => !s.Had))
            {
                return SideVerdict.SourceEmpty;
            }

            if (states.All(s => s.Had) && states.Select(s => s.Copy).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                // All equivalent: send from the oldest storefront that actually holds the rows now.
                var source = states.Select(s => s.Provider).OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).First();
                return new SideVerdict(SideOutcome.Missing, source.Id);
            }

            return SideVerdict.Undetermined(UndeterminedReason.CandidatesDisagree);
        }

        // The (product, published-at) pairs a storefront held at an instant: its publications assigned AND first
        // published before then.
        HashSet<(Guid, DateTimeOffset)> PairsAt(Guid storefrontId, DateTimeOffset at) => publications[storefrontId]
            .Where(p => p.CreatedAt < at && p.ProductPublishedAt < at)
            .Select(p => (p.ProductId, p.ProductPublishedAt))
            .ToHashSet();
    }

    private static readonly StorefrontBackfillTarget[] Sides = [StorefrontBackfillTarget.Payments, StorefrontBackfillTarget.Fulfillment];

    private static string Multiset(IEnumerable<string> keys) => string.Join('\u001e', keys.Order(StringComparer.Ordinal));
}

/// <summary>The sends a set of verdicts selects, per target: Missing sides only, oldest first, capped by --limit.</summary>
public sealed record StorefrontBackfillPlan(
    IReadOnlyList<DuplicationVerdict> Verdicts,
    IReadOnlyList<DuplicationVerdict> Payments,
    IReadOnlyList<DuplicationVerdict> Fulfillment)
{
    public IReadOnlyList<DuplicationVerdict> Selected(StorefrontBackfillTarget target) =>
        target == StorefrontBackfillTarget.Payments ? Payments : Fulfillment;

    public int MissingCount(StorefrontBackfillTarget target) =>
        Verdicts.Count(v => v.Side(target).Outcome == SideOutcome.Missing);

    public static StorefrontBackfillPlan From(IReadOnlyList<DuplicationVerdict> verdicts, int? limit)
    {
        IReadOnlyList<DuplicationVerdict> Pick(StorefrontBackfillTarget target)
        {
            var missing = verdicts.Where(v => v.Side(target).Outcome == SideOutcome.Missing)
                .OrderBy(v => v.CreatedAt).ThenBy(v => v.TargetId);
            return (limit is { } n ? missing.Take(n) : missing).ToList();
        }

        return new StorefrontBackfillPlan(verdicts, Pick(StorefrontBackfillTarget.Payments), Pick(StorefrontBackfillTarget.Fulfillment));
    }
}
