using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

namespace ThreeCommerce.Entity.Tests;

/// <summary>
/// audit_chain_timestamp_precision (ADR-0029 amendment 2026-11-04). The audit hash covers
/// <c>OccurredAt.ToString("O")</c>; Postgres keeps microseconds and Npgsql drops the seventh fractional digit, while a
/// Linux clock fills it. Before the fix an entry hashed at …54.1755249 came back as …54.1755240 and
/// <see cref="AuditChain.Verify"/> called it tampered (~9 in 10 entries written on Linux). New entries are now stamped at
/// stored precision BEFORE hashing; entries written before the fix are still fully re-verified from the one
/// sub-microsecond original (stored + 1..9 ticks) Npgsql can have truncated, and counted as legacy. The clock is pinned to
/// a sub-microsecond tick so the case is deterministic on macOS too (whose clock gives whole microseconds). The reload is
/// simulated here exactly as Npgsql writes (integer microseconds); <c>AuditChainPostgresTests</c> proves it on Postgres.
/// </summary>
public class AuditStoredPrecisionTests
{
    // …54.1755249: the last tick digit (9 × 100 ns) is what Postgres drops.
    private static readonly DateTimeOffset LinuxNow = new DateTimeOffset(2026, 11, 4, 9, 12, 54, TimeSpan.Zero).AddTicks(1_755_249);
    private static readonly DateTimeOffset StoredNow = new DateTimeOffset(2026, 11, 4, 9, 12, 54, TimeSpan.Zero).AddTicks(1_755_240);
    private static readonly Guid Tenant = Guid.NewGuid();

    private static AuditDraft Draft(string action) =>
        new(Tenant, action, "SupplierChangeRequest", "r1", AuditOutcome.Success, Guid.NewGuid(), "admin", "BankAccount");

    /// <summary>What a timestamptz round trip returns: Npgsql writes whole microseconds (integer division).</summary>
    private static AuditEntry Reloaded(AuditEntry entry) =>
        entry with { OccurredAt = new DateTimeOffset(entry.OccurredAt.UtcTicks / 10 * 10, TimeSpan.Zero) };

    /// <summary>An entry exactly as the pre-fix code wrote it: hashed over the untruncated 100 ns time.</summary>
    private static AuditEntry LegacyEntry(AuditEntry? previous, string action, DateTimeOffset occurredAt)
    {
        var entry = new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            TenantId = Tenant,
            Sequence = (previous?.Sequence ?? 0) + 1,
            OccurredAt = occurredAt,
            ActorId = Guid.NewGuid(),
            ActorRole = "admin",
            Action = action,
            ResourceType = "SupplierChangeRequest",
            ResourceId = "r1",
            Outcome = AuditOutcome.Success,
            Summary = "BankAccount",
            PrevHash = previous?.Hash ?? AuditEntry.Genesis,
            Hash = string.Empty,
        };
        return entry with { Hash = entry.ComputeHash() };
    }

    /// <summary>Five pre-fix entries at sub-microsecond ticks 9, 0, 1, 5, 3 (the 0 one round-trips unchanged).</summary>
    private static List<AuditEntry> LegacyChain()
    {
        var chain = new List<AuditEntry>();
        foreach (var subTicks in new[] { 9, 0, 1, 5, 3 })
        {
            var at = StoredNow.AddSeconds(chain.Count).AddTicks(subTicks);
            chain.Add(LegacyEntry(chain.LastOrDefault(), $"legacy.{chain.Count + 1}", at));
        }

        return chain;
    }

    [Fact]
    public void StoredPrecision_truncates_to_whole_microseconds_and_keeps_the_offset()
    {
        Assert.Equal(StoredNow, AuditEntry.StoredPrecision(LinuxNow));
        Assert.Equal(StoredNow, AuditEntry.StoredPrecision(StoredNow)); // already stored precision: unchanged
        var local = LinuxNow.ToOffset(TimeSpan.FromHours(10));
        Assert.Equal(TimeSpan.FromHours(10), AuditEntry.StoredPrecision(local).Offset);
        Assert.Equal(StoredNow, AuditEntry.StoredPrecision(local));
    }

    [Fact]
    public async Task An_entry_recorded_at_a_sub_microsecond_tick_verifies_after_a_Postgres_reload()
    {
        var store = new FakeAuditStore();
        var recorder = new AuditRecorder(store, new FixedClock(LinuxNow));

        var first = await recorder.RecordAsync(Draft("supplier.change_request.approved"), default);
        await recorder.RecordAsync(Draft("supplier.change_request.rejected"), default);
        await recorder.RecordAsync(Draft("field_reveal.account_number"), default);

        var result = AuditChain.Verify(store.Entries.Select(Reloaded).ToList());
        Assert.True(result.Intact, $"broken at {result.FirstBrokenSequence}");
        Assert.Equal(0, result.LegacyPrecisionEntries); // new entries verify exactly, never via the legacy search

        // In memory the entry already holds — and hashed — what Postgres will store.
        Assert.Equal(StoredNow, first.OccurredAt);
        Assert.Equal(first.Hash, first.ComputeHash());
        Assert.Equal(store.Entries.Select(e => e.OccurredAt), store.Entries.Select(Reloaded).Select(e => e.OccurredAt));
    }

    [Fact]
    public void A_chain_written_before_the_fix_verifies_fully_after_a_Postgres_reload_and_counts_its_legacy_entries()
    {
        var legacy = LegacyChain();
        Assert.True(AuditChain.Verify(legacy).Intact); // in memory, before the round trip, it was always fine

        var reloaded = legacy.Select(Reloaded).ToList();
        Assert.NotEqual(legacy[0].OccurredAt, reloaded[0].OccurredAt); // …249 came back …240
        Assert.NotEqual(legacy[0].Hash, reloaded[0].ComputeHash());   // so its exact hash no longer reproduces

        var result = AuditChain.Verify(reloaded);
        Assert.True(result.Intact, $"broken at {result.FirstBrokenSequence}");
        Assert.Equal(4, result.LegacyPrecisionEntries); // the sub-tick-0 entry round-trips and verifies exactly
    }

    [Fact]
    public void New_entries_chain_onto_a_reloaded_legacy_chain_and_the_whole_chain_verifies()
    {
        var reloaded = LegacyChain().Select(Reloaded).ToList();
        var next = AuditChain.Append(reloaded[^1], Draft("supplier.change_request.approved"), LinuxNow.AddMinutes(1));
        var after = AuditChain.Append(next, Draft("supplier.change_request.rejected"), LinuxNow.AddMinutes(2));

        var result = AuditChain.Verify([.. reloaded, Reloaded(next), Reloaded(after)]);
        Assert.True(result.Intact, $"broken at {result.FirstBrokenSequence}");
        Assert.Equal(4, result.LegacyPrecisionEntries);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(-1000)]
    [InlineData(1_000_000)]
    public void Moving_a_legacy_entrys_stored_time_by_any_whole_microsecond_still_breaks_the_chain(int microseconds)
    {
        var reloaded = LegacyChain().Select(Reloaded).ToList();
        reloaded[2] = reloaded[2] with { OccurredAt = reloaded[2].OccurredAt.AddTicks(microseconds * TimeSpan.TicksPerMicrosecond) };

        var result = AuditChain.Verify(reloaded);
        Assert.False(result.Intact);
        Assert.Equal(3, result.FirstBrokenSequence);
    }

    [Fact]
    public void Editing_a_legacy_entry_or_dropping_one_still_breaks_the_chain()
    {
        var edited = LegacyChain().Select(Reloaded).ToList();
        edited[0] = edited[0] with { Summary = "EDITED" };
        Assert.Equal(1, AuditChain.Verify(edited).FirstBrokenSequence);

        var dropped = LegacyChain().Select(Reloaded).ToList();
        dropped.RemoveAt(1);
        Assert.False(AuditChain.Verify(dropped).Intact);
    }

    [Fact]
    public void A_sub_microsecond_time_that_did_not_come_from_Postgres_is_never_searched()
    {
        // Hashed at …249 but presented at …248: no store returns that, so only the exact hash counts.
        var entry = LegacyEntry(null, "legacy.1", LinuxNow);
        var shifted = entry with { OccurredAt = LinuxNow.AddTicks(-1) };

        Assert.False(AuditChain.Verify([shifted]).Intact);
    }

    [Fact]
    public async Task The_publish_only_emitter_publishes_the_stored_precision_time_its_hash_covers()
    {
        var publisher = new CapturingPublishEndpoint();
        var emitter = new AuditEmitter(publisher, new AuditSource("payments"), new FixedClock(LinuxNow), NullLogger<AuditEmitter>.Instance);

        await emitter.RecordAsync(AuditCategories.Mutation(
            Tenant, Guid.NewGuid(), "admin", "PaymentAccount", "acc-1", "payments.payment_account.create", "Main"), default);

        var published = Assert.IsType<AuditEntryRecorded>(Assert.Single(publisher.Published));
        Assert.Equal(StoredNow, published.OccurredAt); // what the central projection stores in Postgres

        var recomputed = new AuditEntry
        {
            TenantId = published.TenantId,
            Sequence = published.Sequence,
            OccurredAt = published.OccurredAt,
            ActorId = published.ActorId,
            ActorRole = published.ActorRole,
            Action = published.Action,
            ResourceType = published.ResourceType,
            ResourceId = published.ResourceId,
            Outcome = AuditOutcome.Success,
            Summary = published.Summary,
            PrevHash = AuditEntry.Genesis,
            Hash = string.Empty,
        };
        Assert.Equal(published.Hash, recomputed.ComputeHash());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingPublishEndpoint : IPublishEndpoint
    {
        public readonly List<object> Published = [];

        public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class
        {
            Published.Add(message);
            return Task.CompletedTask;
        }

        public Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class =>
            Publish(message, cancellationToken);

        public Task Publish<T>(T message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class =>
            Publish(message, cancellationToken);

        public Task Publish(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish(object message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish(object message, Type messageType, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();

        public Task Publish<T>(object values, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();

        public Task Publish<T>(object values, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();

        public ConnectHandle ConnectPublishObserver(IPublishObserver observer) => throw new NotSupportedException();
    }
}
