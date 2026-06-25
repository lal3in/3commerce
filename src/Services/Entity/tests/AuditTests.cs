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

/// <summary>mt6_2 audit coverage rules: categories, deny/sensitive shapes, PII-safety, mixed chain.</summary>
public class SensitiveAuditTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();

    [Fact]
    public void Denied_attempt_records_a_denied_outcome_with_the_reason()
    {
        var draft = AuditCategories.DeniedAttempt(
            Tenant, Actor, "admin", "SupplierChangeRequest", "r1", "supplier.change_request.approve",
            "A change request cannot be decided by its requester (maker-checker).");

        Assert.Equal(AuditOutcome.Denied, draft.Outcome);
        Assert.Contains("maker-checker", draft.Summary);
    }

    [Fact]
    public void Sensitive_read_labels_the_field_and_keeps_the_reason_not_the_value()
    {
        var draft = AuditCategories.SensitiveRead(
            Tenant, Actor, null, "SupplierBankAccount", "b1", "account_number", "support ticket #4821");

        Assert.Equal(AuditOutcome.Success, draft.Outcome);
        Assert.Equal("sensitive_read.account_number", draft.Action);
        Assert.Equal("support ticket #4821", draft.Summary); // the reason, never the value
    }

    [Fact]
    public void Field_reveal_records_who_revealed_what_and_why()
    {
        var draft = AuditCategories.FieldReveal(
            Tenant, Actor, "support", "SupplierBankAccount", "b1", "bsb", "customer verification call");

        Assert.Equal("field_reveal.bsb", draft.Action);
        Assert.Equal(Actor, draft.ActorId);
        Assert.Equal("customer verification call", draft.Summary);
    }

    [Fact]
    public async Task Mixed_category_entries_chain_and_stay_verifiable()
    {
        var store = new FakeAuditStore();
        var recorder = new AuditRecorder(store, TimeProvider.System);

        await recorder.RecordAsync(AuditCategories.Mutation(Tenant, Actor, null, "SupplierChangeRequest", "r1", "supplier.change_request.approved", "BankAccount"), default);
        await recorder.RecordAsync(AuditCategories.DeniedAttempt(Tenant, Actor, null, "SupplierChangeRequest", "r2", "supplier.change_request.approve", "maker-checker"), default);
        await recorder.RecordAsync(AuditCategories.SensitiveRead(Tenant, Actor, null, "SupplierBankAccount", "b1", "account_number", "support ticket #4821"), default);

        Assert.True((await recorder.VerifyAsync(Tenant, default)).Intact);
        Assert.Equal(AuditOutcome.Denied, store.Entries[1].Outcome);
    }
}

/// <summary>In-memory audit store for the framework tests (mt6_1/mt6_2).</summary>
internal sealed class FakeAuditStore : IAuditStore
{
    public readonly List<AuditEntry> Entries = [];

    public Task<AuditEntry?> LastAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult(Entries.Where(e => e.TenantId == tenantId).OrderByDescending(e => e.Sequence).FirstOrDefault());

    public void Add(AuditEntry entry) => Entries.Add(entry);

    public Task<List<AuditEntry>> ChainAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult(Entries.Where(e => e.TenantId == tenantId).OrderBy(e => e.Sequence).ToList());
}
