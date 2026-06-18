using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Tests;

public class SupplierChangeRequestTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();

    private static SupplierChangeRequest NewPending() => SupplierChangeRequest.Open(
        Tenant, Entity, SupplierChangeRequestType.BankAccount, "Update payout account", "BSB ****123", Requester, DateTimeOffset.UtcNow);

    [Fact]
    public void Open_starts_pending()
    {
        var req = NewPending();
        Assert.Equal(SupplierChangeRequestStatus.Pending, req.Status);
        Assert.Equal(Requester, req.RequestedByPrincipalId);
        Assert.Null(req.DecidedByPrincipalId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Open_requires_a_summary(string summary)
    {
        Assert.Throws<DomainRuleException>(() => SupplierChangeRequest.Open(
            Tenant, Entity, SupplierChangeRequestType.Contact, summary, null, Requester, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_different_principal_can_approve()
    {
        var req = NewPending();
        req.Approve(Approver, "looks good", DateTimeOffset.UtcNow);

        Assert.Equal(SupplierChangeRequestStatus.Approved, req.Status);
        Assert.Equal(Approver, req.DecidedByPrincipalId);
        Assert.Equal("looks good", req.DecisionReason);
        Assert.NotNull(req.DecidedAt);
    }

    [Fact]
    public void The_requester_cannot_approve_their_own_request()
    {
        var req = NewPending();
        var ex = Assert.Throws<DomainRuleException>(() => req.Approve(Requester, null, DateTimeOffset.UtcNow));
        Assert.Contains("maker-checker", ex.Message);
    }

    [Fact]
    public void Rejection_requires_a_reason()
    {
        var req = NewPending();
        Assert.Throws<DomainRuleException>(() => req.Reject(Approver, "  ", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Rejection_records_the_decision()
    {
        var req = NewPending();
        req.Reject(Approver, "incomplete details", DateTimeOffset.UtcNow);
        Assert.Equal(SupplierChangeRequestStatus.Rejected, req.Status);
        Assert.Equal("incomplete details", req.DecisionReason);
    }

    [Fact]
    public void A_decided_request_cannot_be_decided_again()
    {
        var req = NewPending();
        req.Approve(Approver, null, DateTimeOffset.UtcNow);
        Assert.Throws<DomainRuleException>(() => req.Reject(Approver, "changed my mind", DateTimeOffset.UtcNow));
    }
}
