using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Tools.OrderConfirmedBackfill.Storefronts;
using static ThreeCommerce.Tools.OrderConfirmedBackfill.Tests.StorefrontWorld;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Tests;

public class StorefrontBackfillPlannerTests
{
    private static readonly TimeSpan Lag = TimeSpan.FromMilliseconds(40); // consumer lag after the duplicate is created

    /// <summary>A source with published products, two payment accounts and a carrier.</summary>
    private static (StorefrontWorld World, StorefrontFact Source) FullSource()
    {
        var w = new StorefrontWorld();
        var source = w.Store("Main", T0);
        w.Publish(source, T0.AddMinutes(1));
        w.Publish(source, T0.AddMinutes(2));
        w.Account(source, "Card", T0.AddMinutes(3));
        w.Account(source, "Wallet", T0.AddMinutes(3), provider: "stripe", externalRef: "acct_1");
        w.Carrier(source, CarrierCode.Fake, T0.AddMinutes(4), activate: true);
        return (w, source);
    }

    [Fact]
    public void Each_split_copy_is_selected_for_exactly_the_side_it_lost_from_its_source()
    {
        var (w, s) = FullSource();
        var t1 = w.Duplicate(s, "Copy 1", T0.AddHours(1));
        w.PaymentsConsumer(s.Id, t1.Id, t1.CreatedAt + Lag);          // the event reached Payments only
        var t2 = w.Duplicate(s, "Copy 2", T0.AddHours(2));
        w.FulfillmentConsumer(s.Id, t2.Id, t2.CreatedAt + Lag);       // … Fulfillment only
        var t3 = w.Duplicate(s, "Copy 3", T0.AddHours(3));
        w.PaymentsConsumer(s.Id, t3.Id, t3.CreatedAt + Lag);

        var v1 = w.Verdict(t1);
        Assert.Equal(SideVerdict.Present, v1.Payments);
        Assert.Equal(new SideVerdict(SideOutcome.Missing, s.Id), v1.Fulfillment);
        Assert.Equal("publications+copied-payments", v1.Evidence);

        var v2 = w.Verdict(t2);
        Assert.Equal(new SideVerdict(SideOutcome.Missing, s.Id), v2.Payments);
        Assert.Equal(SideVerdict.Present, v2.Fulfillment);

        var v3 = w.Verdict(t3);
        Assert.Equal(new SideVerdict(SideOutcome.Missing, s.Id), v3.Fulfillment);

        var plan = StorefrontBackfillPlan.From(w.Plan(), null);
        Assert.Equal([t2.Id], plan.Payments.Select(v => v.TargetId));
        Assert.Equal([t1.Id, t3.Id], plan.Fulfillment.Select(v => v.TargetId));
    }

    [Fact]
    public void After_the_backfill_is_delivered_every_copy_is_complete_and_a_rerun_selects_nothing()
    {
        var (w, s) = FullSource();
        var t1 = w.Duplicate(s, "Copy 1", T0.AddHours(1));
        w.PaymentsConsumer(s.Id, t1.Id, t1.CreatedAt + Lag);
        var t2 = w.Duplicate(s, "Copy 2", T0.AddHours(2));
        w.FulfillmentConsumer(s.Id, t2.Id, t2.CreatedAt + Lag);

        var first = StorefrontBackfillPlan.From(w.Plan(), null);
        w.Deliver(first, T0.AddDays(30));
        var rerun = StorefrontBackfillPlan.From(w.Plan(), null);

        Assert.Empty(rerun.Payments);
        Assert.Empty(rerun.Fulfillment);
        Assert.All(rerun.Verdicts, v => Assert.Equal((SideOutcome.Present, SideOutcome.Present), (v.Payments.Outcome, v.Fulfillment.Outcome)));
        // Each copy now holds exactly what its source holds — no duplicates rows, nothing extra.
        foreach (var t in new[] { t1, t2 })
        {
            Assert.Equal(Keys(w.Accounts.Where(a => a.StorefrontId == s.Id).Select(ConfigRowFact.From)),
                Keys(w.Accounts.Where(a => a.StorefrontId == t.Id).Select(ConfigRowFact.From)));
            Assert.Equal(Keys(w.Carriers.Where(c => c.StorefrontId == s.Id).Select(ConfigRowFact.From)),
                Keys(w.Carriers.Where(c => c.StorefrontId == t.Id).Select(ConfigRowFact.From)));
        }
    }

    [Fact]
    public void Complete_copies_and_sources_that_had_nothing_to_copy_select_nothing()
    {
        var (w, s) = FullSource();
        var complete = w.Duplicate(s, "Both", T0.AddHours(1));
        w.PaymentsConsumer(s.Id, complete.Id, complete.CreatedAt + Lag);
        w.FulfillmentConsumer(s.Id, complete.Id, complete.CreatedAt + Lag);

        var paymentsOnly = w.Store("Payments only", T0);
        w.Publish(paymentsOnly, T0.AddMinutes(1));
        w.Account(paymentsOnly, "Card", T0.AddMinutes(2));
        var copy = w.Duplicate(paymentsOnly, "Copy", T0.AddHours(1));
        w.PaymentsConsumer(paymentsOnly.Id, copy.Id, copy.CreatedAt + Lag);
        // The source gets a carrier only AFTER it was duplicated: there was nothing to copy at the time.
        w.Carrier(paymentsOnly, CarrierCode.Fake, T0.AddHours(2));

        Assert.Equal((SideOutcome.Present, SideOutcome.Present), (w.Verdict(complete).Payments.Outcome, w.Verdict(complete).Fulfillment.Outcome));
        Assert.Equal(SideVerdict.Present, w.Verdict(copy).Payments);
        Assert.Equal(SideVerdict.SourceEmpty, w.Verdict(copy).Fulfillment);
        var plan = StorefrontBackfillPlan.From(w.Plan(), null);
        Assert.Empty(plan.Payments);
        Assert.Empty(plan.Fulfillment);
    }

    [Fact]
    public void Config_the_owner_added_to_a_duplicate_is_never_overwritten()
    {
        var (w, s) = FullSource();
        var t = w.Duplicate(s, "Copy", T0.AddHours(1));
        w.PaymentsConsumer(s.Id, t.Id, t.CreatedAt + Lag);
        w.Carrier(t, CarrierCode.Dhl, T0.AddDays(2));                 // carriers configured by hand, later

        var v = w.Verdict(t);
        Assert.Equal(SideVerdict.Present, v.Fulfillment);
        Assert.Empty(StorefrontBackfillPlan.From(w.Plan(), null).Fulfillment);
    }

    [Fact]
    public void A_lookalike_storefront_outside_the_publication_lineage_is_never_the_source()
    {
        var (w, s) = FullSource();
        // Same payment accounts, different carrier, its own publications — an unrelated store that looks alike.
        var lookalike = w.Store("Lookalike", T0.AddMinutes(10));
        w.Publish(lookalike, T0.AddMinutes(11));
        w.Account(lookalike, "Card", T0.AddMinutes(12));
        w.Account(lookalike, "Wallet", T0.AddMinutes(12), provider: "stripe", externalRef: "acct_1");
        w.Carrier(lookalike, CarrierCode.AustraliaPost, T0.AddMinutes(13));
        var t = w.Duplicate(s, "Copy", T0.AddHours(1));
        w.PaymentsConsumer(s.Id, t.Id, t.CreatedAt + Lag);

        var v = w.Verdict(t);
        Assert.Equal(1, v.Candidates);
        Assert.Equal(new SideVerdict(SideOutcome.Missing, s.Id), v.Fulfillment);
    }

    [Fact]
    public void Candidates_that_would_copy_different_things_are_undetermined_unless_the_copy_name_names_one()
    {
        var (w, s) = FullSource();
        var sibling = w.Duplicate(s, "Sibling", T0.AddHours(1));
        w.PaymentsConsumer(s.Id, sibling.Id, sibling.CreatedAt + Lag);
        w.FulfillmentConsumer(s.Id, sibling.Id, sibling.CreatedAt + Lag);
        w.Carrier(sibling, CarrierCode.Dhl, T0.AddHours(2));          // the sibling's carriers now differ from the source's

        var anonymous = w.Duplicate(s, "Spring sale", T0.AddHours(3));
        w.PaymentsConsumer(s.Id, anonymous.Id, anonymous.CreatedAt + Lag);
        var named = w.Duplicate(s, "Main (copy)", T0.AddHours(4));
        w.PaymentsConsumer(s.Id, named.Id, named.CreatedAt + Lag);

        var a = w.Verdict(anonymous);
        Assert.Equal(2, a.Candidates);
        Assert.Equal(SideVerdict.Undetermined(UndeterminedReason.CandidatesDisagree), a.Fulfillment);

        var n = w.Verdict(named);
        Assert.Equal(new SideVerdict(SideOutcome.Missing, s.Id), n.Fulfillment);
        Assert.EndsWith("+name", n.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_copied_publication_only_a_copy_name_links_a_duplicate_to_its_source()
    {
        var w = new StorefrontWorld();
        var s = w.Store("Bare", T0);                                  // nothing published
        w.Account(s, "Card", T0.AddMinutes(1));
        w.Carrier(s, CarrierCode.Fake, T0.AddMinutes(1));
        var custom = w.Duplicate(s, "Whatever", T0.AddHours(1));
        w.PaymentsConsumer(s.Id, custom.Id, custom.CreatedAt + Lag);
        var defaultName = w.Duplicate(s, "Bare (copy)", T0.AddHours(2));
        w.PaymentsConsumer(s.Id, defaultName.Id, defaultName.CreatedAt + Lag);

        Assert.Equal(SideVerdict.Undetermined(UndeterminedReason.NoLineage), w.Verdict(custom).Fulfillment);
        Assert.Equal(SideVerdict.Present, w.Verdict(custom).Payments);
        var v = w.Verdict(defaultName);
        Assert.Equal(new SideVerdict(SideOutcome.Missing, s.Id), v.Fulfillment);
        Assert.Equal("name+copied-payments", v.Evidence);
    }

    [Fact]
    public void A_copied_half_no_candidate_held_leaves_the_duplicate_undetermined()
    {
        var (w, s) = FullSource();
        var t = w.Duplicate(s, "Copy", T0.AddHours(1));
        // A carrier on the duplicate within the clone window that the source never had (configured by hand right away).
        w.Carrier(t, CarrierCode.Ups, t.CreatedAt.AddMinutes(1));

        var v = w.Verdict(t);
        Assert.Equal(SideVerdict.Present, v.Fulfillment);
        Assert.Equal(SideVerdict.Undetermined(UndeterminedReason.NoCandidate), v.Payments);
    }

    [Fact]
    public void A_chain_of_split_copies_resolves_in_one_run_from_the_storefront_that_holds_the_rows()
    {
        var (w, s) = FullSource();
        var t1 = w.Duplicate(s, "Copy 1", T0.AddHours(1));
        w.PaymentsConsumer(s.Id, t1.Id, t1.CreatedAt + Lag);          // t1 lost its carriers
        var t2 = w.Duplicate(t1, "Copy 1 (copy)", T0.AddHours(2));    // a copy of the copy …
        w.FulfillmentConsumer(t1.Id, t2.Id, t2.CreatedAt + Lag);      // … reached Fulfillment, which found nothing on t1

        var v2 = w.Verdict(t2);
        // Named after t1: its payment accounts come from t1, and its carriers from s — the storefront t1 itself is
        // being backfilled from in this same run (t1 holds no carrier row yet to copy from).
        Assert.Equal(new SideVerdict(SideOutcome.Missing, t1.Id), v2.Payments);
        Assert.Equal(new SideVerdict(SideOutcome.Missing, s.Id), v2.Fulfillment);
        Assert.Equal(new SideVerdict(SideOutcome.Missing, s.Id), w.Verdict(t1).Fulfillment);

        var plan = StorefrontBackfillPlan.From(w.Plan(), null);
        w.Deliver(plan, T0.AddDays(30));
        var rerun = StorefrontBackfillPlan.From(w.Plan(), null);
        Assert.Empty(rerun.Payments);
        Assert.Empty(rerun.Fulfillment);
    }

    [Fact]
    public void Archived_and_vanished_duplicates_are_skipped()
    {
        var (w, s) = FullSource();
        var archived = w.Duplicate(s, "Old", T0.AddHours(1));
        w.Archive(archived);
        var gone = w.Duplicate(s, "Gone", T0.AddHours(2));
        w.Vanish(gone);

        Assert.All(w.Plan(), v => Assert.Equal((SideOutcome.Skipped, SideOutcome.Skipped), (v.Payments.Outcome, v.Fulfillment.Outcome)));
    }

    [Fact]
    public void A_duplicate_without_an_audit_entry_is_found_from_its_copied_publications_and_keeps_its_current_name()
    {
        var (w, s) = FullSource();
        var t = w.Duplicate(s, "Unaudited", T0.AddHours(1), audited: false);
        w.FulfillmentConsumer(s.Id, t.Id, t.CreatedAt + Lag);

        var v = w.Verdict(t);
        Assert.Equal("Unaudited", v.Name);
        Assert.Equal(new SideVerdict(SideOutcome.Missing, s.Id), v.Payments);
    }

    [Fact]
    public void Limit_sends_the_oldest_duplicates_first_per_target()
    {
        var (w, s) = FullSource();
        var copies = Enumerable.Range(1, 4).Select(i =>
        {
            var t = w.Duplicate(s, $"Copy {i}", T0.AddHours(i));
            w.PaymentsConsumer(s.Id, t.Id, t.CreatedAt + Lag);
            return t;
        }).ToList();

        var plan = StorefrontBackfillPlan.From(w.Plan(), limit: 2);

        Assert.Equal(copies.Take(2).Select(c => c.Id), plan.Fulfillment.Select(v => v.TargetId));
        Assert.Equal(4, plan.MissingCount(StorefrontBackfillTarget.Fulfillment));
    }

    [Fact]
    public void The_rebuilt_event_carries_tenant_source_duplicate_and_the_name_it_was_created_with()
    {
        var (w, s) = FullSource();
        var t = w.Duplicate(s, "Main (copy)", T0.AddHours(1));
        w.PaymentsConsumer(s.Id, t.Id, t.CreatedAt + Lag);
        var v = w.Verdict(t);

        var e = StorefrontBackfillQueues.Rebuild(v, StorefrontBackfillTarget.Fulfillment);

        Assert.Equal(new BuildingBlocks.Contracts.Catalog.StorefrontDuplicated(w.Tenant, s.Id, t.Id, "Main (copy)"), e);
        Assert.Throws<InvalidOperationException>(() => StorefrontBackfillQueues.Rebuild(v, StorefrontBackfillTarget.Payments));
    }

    [Fact]
    public void Copy_keys_equal_exactly_what_the_services_own_clone_produces()
    {
        var w = new StorefrontWorld();
        var s = w.Store("S", T0);
        var account = w.Account(s, "Wallet", T0, provider: "stripe", externalRef: "acct_9");
        var carrier = w.Carrier(s, CarrierCode.Fake, T0, activate: true);
        var target = Guid.CreateVersion7();

        Assert.Equal(ConfigRowFact.From(account).CopyKey, ConfigRowFact.From(account.CloneForStorefront(target, T0.AddDays(1))).CopyKey);
        Assert.Equal(ConfigRowFact.From(carrier).CopyKey, ConfigRowFact.From(carrier.CloneForStorefront(target, T0.AddDays(1))).CopyKey);

        // A lifecycle change alters what a copy would carry, but not the match key the source is recognised by.
        var before = ConfigRowFact.From(carrier);
        carrier.Suspend(T0.AddDays(2));
        Assert.NotEqual(before.CopyKey, ConfigRowFact.From(carrier).CopyKey);
        Assert.Equal(before.MatchKey, ConfigRowFact.From(carrier).MatchKey);
    }

    private static List<string> Keys(IEnumerable<ConfigRowFact> rows) => rows.Select(r => r.CopyKey).Order(StringComparer.Ordinal).ToList();
}
