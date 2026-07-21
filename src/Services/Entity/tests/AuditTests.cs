using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Streams;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Streams;
using ThreeCommerce.Entity.Domain;
using ThreeCommerce.Entity.Infrastructure;

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

    [Fact]
    public async Task Recording_can_stage_redacted_audit_fact_to_stream_outbox()
    {
        var auditStore = new FakeAuditStore();
        var outboxStore = new InMemoryStreamOutboxStore();
        var recorder = new AuditRecorder(auditStore, TimeProvider.System, streamOutbox: new StreamOutboxStager(outboxStore));

        var entry = await recorder.RecordAsync(new AuditDraft(
            Tenant,
            "field_reveal.account_number",
            "SupplierBankAccount",
            "bank-account-1",
            AuditOutcome.Success,
            Guid.CreateVersion7(),
            "support",
            "support ticket #4821"), default);

        var message = Assert.Single(outboxStore.Messages);
        var envelope = JsonSerializer.Deserialize<StreamEventEnvelope<AuditEntryStreamPayload>>(message.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(envelope);
        Assert.Equal(StreamTopics.AuditEntries, message.Topic);
        Assert.Equal(StreamPrivacyClass.Internal, envelope.PrivacyClass);
        Assert.Equal(StreamPartitionKeys.Tenant(Tenant), message.Key);
        Assert.Equal(entry.Hash, envelope.Payload.Hash);
        Assert.Equal("support ticket #4821", envelope.Payload.Summary);
        Assert.DoesNotContain("123456", message.PayloadJson);
        Assert.DoesNotContain("account_number=", message.PayloadJson);
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

/// <summary>
/// mt6_1: the supplier change-request service must record the DECIDING principal's role on every
/// audit entry it writes — approved, rejected, and the maker-checker denial. The role used to be a
/// hard-coded null, which made "who, in which role, approved this payout change" unanswerable.
/// </summary>
public class SupplierChangeRequestAuditTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();

    private sealed record Harness(SupplierChangeRequestService Service, FakeAuditStore Store, EntityDbContext Db);

    private static Harness NewHarness()
    {
        var db = new EntityDbContext(new DbContextOptionsBuilder<EntityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var store = new FakeAuditStore();
        var service = new SupplierChangeRequestService(db, new AuditRecorder(store, TimeProvider.System), TimeProvider.System);
        return new Harness(service, store, db);
    }

    private static async Task<SupplierChangeRequest> OpenAsync(Harness harness) =>
        await harness.Service.OpenAsync(
            Tenant, Guid.NewGuid(), SupplierChangeRequestType.BankAccount, "Update payout account", "BSB ****123", Requester, default);

    [Fact]
    public async Task Approval_records_the_deciding_principals_role()
    {
        var harness = NewHarness();
        var request = await OpenAsync(harness);

        await harness.Service.ApproveAsync(Tenant, request.Id, Approver, "tenant_admin", "looks good", default);

        var entry = Assert.Single(harness.Store.Entries);
        Assert.Equal("supplier.change_request.approved", entry.Action);
        Assert.Equal(Approver, entry.ActorId);
        Assert.Equal("tenant_admin", entry.ActorRole);
    }

    [Fact]
    public async Task Rejection_records_the_deciding_principals_role()
    {
        var harness = NewHarness();
        var request = await OpenAsync(harness);

        await harness.Service.RejectAsync(Tenant, request.Id, Approver, "compliance", "incomplete details", default);

        var entry = Assert.Single(harness.Store.Entries);
        Assert.Equal("supplier.change_request.rejected", entry.Action);
        Assert.Equal("compliance", entry.ActorRole);
    }

    [Fact]
    public async Task A_maker_checker_denial_records_the_attempting_principals_role()
    {
        var harness = NewHarness();
        var request = await OpenAsync(harness);

        // The requester deciding their own request is denied (ADR-0025) and audited as such.
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            harness.Service.ApproveAsync(Tenant, request.Id, Requester, "tenant_admin", null, default));

        var entry = Assert.Single(harness.Store.Entries);
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        Assert.Equal(Requester, entry.ActorId);
        Assert.Equal("tenant_admin", entry.ActorRole);
        Assert.Contains("maker-checker", entry.Summary);
    }

    [Fact]
    public async Task An_unauthenticated_actor_still_records_a_null_role_rather_than_failing()
    {
        var harness = NewHarness();
        var request = await OpenAsync(harness);

        await harness.Service.ApproveAsync(Tenant, request.Id, Approver, null, null, default);

        Assert.Null(Assert.Single(harness.Store.Entries).ActorRole);
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
