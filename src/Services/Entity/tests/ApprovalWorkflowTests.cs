using ThreeCommerce.BuildingBlocks.Infrastructure.Approval;

namespace ThreeCommerce.Entity.Tests;

public class ApprovalWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Requester = Guid.NewGuid();

    private static ApprovalTask Open(ApprovalRisk risk = ApprovalRisk.Medium) =>
        ApprovalTask.Open(Tenant, "supplier.bank.change", "SupplierChangeRequest", "r1", Requester, risk, Now);

    [Fact]
    public void Requester_cannot_decide_their_own_task()
    {
        Assert.Throws<ApprovalRuleException>(() => Open().Decide(new ApprovalActor(Requester), approve: true, "self", Now));
    }

    [Fact]
    public void Service_accounts_cannot_approve()
    {
        Assert.Throws<ApprovalRuleException>(
            () => Open().Decide(new ApprovalActor(Guid.NewGuid(), IsServiceAccount: true), approve: true, "bot", Now));
    }

    [Fact]
    public void A_different_human_can_approve()
    {
        var task = Open();
        task.Decide(new ApprovalActor(Guid.NewGuid()), approve: true, "looks good", Now);

        Assert.Equal(ApprovalStatus.Approved, task.Status);
        Assert.False(task.WasOverride);
        Assert.NotNull(task.DecidedAt);
    }

    [Fact]
    public void Rejecting_requires_a_reason()
    {
        Assert.Throws<ApprovalRuleException>(() => Open().Decide(new ApprovalActor(Guid.NewGuid()), approve: false, null, Now));
    }

    [Fact]
    public void MasterGlobal_may_override_maker_checker_with_a_reason()
    {
        var task = Open();
        task.Decide(new ApprovalActor(Requester, IsMasterGlobal: true), approve: true, "platform override #12", Now);

        Assert.Equal(ApprovalStatus.Approved, task.Status);
        Assert.True(task.WasOverride);
    }

    [Fact]
    public void MasterGlobal_override_without_a_reason_is_rejected()
    {
        Assert.Throws<ApprovalRuleException>(
            () => Open().Decide(new ApprovalActor(Requester, IsMasterGlobal: true), approve: true, null, Now));
    }

    [Fact]
    public void A_decided_task_cannot_be_decided_again()
    {
        var task = Open();
        task.Decide(new ApprovalActor(Guid.NewGuid()), approve: true, "ok", Now);
        Assert.Throws<ApprovalRuleException>(() => task.Decide(new ApprovalActor(Guid.NewGuid()), approve: true, "again", Now));
    }

    [Fact]
    public void An_expired_task_cannot_be_decided()
    {
        Assert.Throws<ApprovalRuleException>(
            () => Open(ApprovalRisk.High).Decide(new ApprovalActor(Guid.NewGuid()), approve: true, "late", Now.AddDays(2)));
    }

    [Fact]
    public void Expire_sweeps_a_pending_task_past_its_deadline()
    {
        var task = Open(ApprovalRisk.High);
        Assert.True(task.Expire(Now.AddDays(2)));
        Assert.Equal(ApprovalStatus.Expired, task.Status);
        Assert.False(task.Expire(Now.AddDays(3))); // already expired — no further transition
    }

    [Fact]
    public void Expiry_window_shortens_as_risk_rises()
    {
        Assert.True(ApprovalTask.ExpiryFor(ApprovalRisk.High) < ApprovalTask.ExpiryFor(ApprovalRisk.Medium));
        Assert.True(ApprovalTask.ExpiryFor(ApprovalRisk.Medium) < ApprovalTask.ExpiryFor(ApprovalRisk.Low));
    }
}
