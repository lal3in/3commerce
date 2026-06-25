using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

namespace ThreeCommerce.Entity.Tests;

public class AuditChainTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();

    private static AuditDraft Draft(string action) =>
        new(Tenant, action, "SupplierChangeRequest", Guid.NewGuid().ToString(), AuditOutcome.Success, Guid.NewGuid(), Summary: "BankAccount");

    [Fact]
    public void Append_chains_hashes_and_increments_sequence()
    {
        var first = AuditChain.Append(null, Draft("a"), Now);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(AuditEntry.Genesis, first.PrevHash);
        Assert.NotEmpty(first.Hash);

        var second = AuditChain.Append(first, Draft("b"), Now);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.Hash, second.PrevHash);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void Verify_passes_for_an_intact_chain()
    {
        var first = AuditChain.Append(null, Draft("a"), Now);
        var second = AuditChain.Append(first, Draft("b"), Now);
        var third = AuditChain.Append(second, Draft("c"), Now);
        Assert.True(AuditChain.Verify([first, second, third]).Intact);
    }

    [Fact]
    public void Verify_detects_an_edited_entry()
    {
        var first = AuditChain.Append(null, Draft("a"), Now);
        var second = AuditChain.Append(first, Draft("b"), Now);

        var tampered = second with { Summary = "EDITED" }; // hash no longer matches content

        var result = AuditChain.Verify([first, tampered]);
        Assert.False(result.Intact);
        Assert.Equal(2, result.FirstBrokenSequence);
    }

    [Fact]
    public void Verify_detects_a_deleted_entry()
    {
        var first = AuditChain.Append(null, Draft("a"), Now);
        var second = AuditChain.Append(first, Draft("b"), Now);
        var third = AuditChain.Append(second, Draft("c"), Now);

        // Remove the middle entry: third no longer links to first, and the sequence skips.
        Assert.False(AuditChain.Verify([first, third]).Intact);
    }
}

public class AuditRecorderTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static AuditDraft Draft(string action) =>
        new(Tenant, action, "SupplierChangeRequest", "r1", AuditOutcome.Success, Guid.NewGuid());

    private sealed class FakeAuditStore : IAuditStore
    {
        public readonly List<AuditEntry> Entries = [];

        public Task<AuditEntry?> LastAsync(Guid tenantId, CancellationToken ct) =>
            Task.FromResult(Entries.Where(e => e.TenantId == tenantId).OrderByDescending(e => e.Sequence).FirstOrDefault());

        public void Add(AuditEntry entry) => Entries.Add(entry);

        public Task<List<AuditEntry>> ChainAsync(Guid tenantId, CancellationToken ct) =>
            Task.FromResult(Entries.Where(e => e.TenantId == tenantId).OrderBy(e => e.Sequence).ToList());
    }

    [Fact]
    public async Task Recording_chains_entries_and_verifies_intact()
    {
        var store = new FakeAuditStore();
        var recorder = new AuditRecorder(store, TimeProvider.System);

        await recorder.RecordAsync(Draft("supplier.change_request.approved"), default);
        await recorder.RecordAsync(Draft("supplier.change_request.rejected"), default);

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(store.Entries[0].Hash, store.Entries[1].PrevHash);
        Assert.True((await recorder.VerifyAsync(Tenant, default)).Intact);
    }

    [Fact]
    public async Task Verify_through_the_recorder_catches_tampering()
    {
        var store = new FakeAuditStore();
        var recorder = new AuditRecorder(store, TimeProvider.System);
        await recorder.RecordAsync(Draft("a"), default);
        await recorder.RecordAsync(Draft("b"), default);

        store.Entries[0] = store.Entries[0] with { Summary = "EDITED" };

        var result = await recorder.VerifyAsync(Tenant, default);
        Assert.False(result.Intact);
        Assert.Equal(1, result.FirstBrokenSequence);
    }
}
