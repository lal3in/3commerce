using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.Entity.Domain;
using ThreeCommerce.Entity.Infrastructure;

namespace ThreeCommerce.Entity.Tests;

/// <summary>
/// The supplier-portal "approval lock" (ADR-0025): a supplier may edit its own entity details
/// directly ONLY before the tenant approves it. Once Active, direct edits are refused and every
/// change must flow through a maker-checker change request whose approval applies the new values.
/// </summary>
public class SupplierSelfServiceTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();

    [Theory]
    [InlineData(SupplierOnboardingState.Draft)]
    [InlineData(SupplierOnboardingState.PendingVerification)]
    [InlineData(SupplierOnboardingState.PendingApproval)]
    public void Direct_edit_is_allowed_before_activation(SupplierOnboardingState state)
    {
        var onboarding = OnboardingIn(state);

        Assert.True(onboarding.AllowsDirectDetailEdit);
        onboarding.EnsureDirectDetailEditAllowed(); // does not throw
    }

    [Theory]
    [InlineData(SupplierOnboardingState.Active)]
    [InlineData(SupplierOnboardingState.Suspended)]
    [InlineData(SupplierOnboardingState.Archived)]
    public void Direct_edit_is_locked_once_approved(SupplierOnboardingState state)
    {
        var onboarding = OnboardingIn(state);

        Assert.False(onboarding.AllowsDirectDetailEdit);
        var ex = Assert.Throws<DomainRuleException>(onboarding.EnsureDirectDetailEditAllowed);
        Assert.Contains("change request", ex.Message);
    }

    [Fact]
    public async Task Approving_an_entity_details_request_applies_the_new_names()
    {
        var (service, db) = NewService();
        var entity = EntityRecord.Create(Tenant, EntityType.Company, "Old Legal Pty Ltd", "Old Trading", DateTimeOffset.UtcNow, []);
        db.Entities.Add(entity);
        await db.SaveChangesAsync();

        var detail = System.Text.Json.JsonSerializer.Serialize(new { legalName = "New Legal Pty Ltd", tradingName = "New Trading" });
        var request = await service.OpenAsync(Tenant, entity.Id, SupplierChangeRequestType.EntityDetails, "Rebrand", detail, Requester, default);

        await service.ApproveAsync(Tenant, request.Id, Approver, "tenant_admin", "approved", default);

        var updated = await db.Entities.SingleAsync(e => e.Id == entity.Id);
        Assert.Equal("New Legal Pty Ltd", updated.LegalName);
        Assert.Equal("New Trading", updated.TradingName);
        Assert.Equal("New Trading", updated.DisplayName);
    }

    [Fact]
    public async Task Rejecting_an_entity_details_request_leaves_the_names_unchanged()
    {
        var (service, db) = NewService();
        var entity = EntityRecord.Create(Tenant, EntityType.Company, "Old Legal Pty Ltd", "Old Trading", DateTimeOffset.UtcNow, []);
        db.Entities.Add(entity);
        await db.SaveChangesAsync();

        var detail = System.Text.Json.JsonSerializer.Serialize(new { legalName = "New Legal Pty Ltd", tradingName = "New Trading" });
        var request = await service.OpenAsync(Tenant, entity.Id, SupplierChangeRequestType.EntityDetails, "Rebrand", detail, Requester, default);

        await service.RejectAsync(Tenant, request.Id, Approver, "tenant_admin", "not verified yet", default);

        var unchanged = await db.Entities.SingleAsync(e => e.Id == entity.Id);
        Assert.Equal("Old Legal Pty Ltd", unchanged.LegalName);
        Assert.Equal("Old Trading", unchanged.TradingName);
    }

    [Fact]
    public async Task Approving_a_non_entity_details_request_does_not_touch_the_entity()
    {
        var (service, db) = NewService();
        var entity = EntityRecord.Create(Tenant, EntityType.Company, "Old Legal Pty Ltd", "Old Trading", DateTimeOffset.UtcNow, []);
        db.Entities.Add(entity);
        await db.SaveChangesAsync();

        var request = await service.OpenAsync(Tenant, entity.Id, SupplierChangeRequestType.BankAccount, "Rotate payout", "BSB ****999", Requester, default);
        await service.ApproveAsync(Tenant, request.Id, Approver, "tenant_admin", "approved", default);

        var unchanged = await db.Entities.SingleAsync(e => e.Id == entity.Id);
        Assert.Equal("Old Legal Pty Ltd", unchanged.LegalName);
        Assert.Equal("Old Trading", unchanged.TradingName);
    }

    private static SupplierOnboarding OnboardingIn(SupplierOnboardingState target)
    {
        var entity = NewReadySupplier();
        var onboarding = SupplierOnboarding.Start(entity, DateTimeOffset.UtcNow);
        if (target == SupplierOnboardingState.Draft)
        {
            return onboarding;
        }

        onboarding.SubmitForVerification(entity, DateTimeOffset.UtcNow);
        if (target == SupplierOnboardingState.PendingVerification)
        {
            return onboarding;
        }

        onboarding.MarkVerificationComplete(DateTimeOffset.UtcNow);
        if (target == SupplierOnboardingState.PendingApproval)
        {
            return onboarding;
        }

        onboarding.Activate(DateTimeOffset.UtcNow);
        if (target == SupplierOnboardingState.Active)
        {
            return onboarding;
        }

        if (target == SupplierOnboardingState.Suspended)
        {
            onboarding.Suspend("Compliance review pending", DateTimeOffset.UtcNow);
            return onboarding;
        }

        onboarding.Archive(DateTimeOffset.UtcNow);
        return onboarding;
    }

    private static EntityRecord NewReadySupplier()
    {
        var entity = EntityRecord.Create(Guid.CreateVersion7(), EntityType.Company, "Acme Pty Ltd", null, DateTimeOffset.UtcNow, []);
        var identifier = entity.AddIdentifier(EntityIdentifierType.Abn, "12345678901", DateTimeOffset.UtcNow);
        identifier.VerificationStatus = EntityVerificationStatus.Verified;
        entity.AddContactMethod(EntityContactPurpose.Primary, EntityContactKind.Email, "supplier@example.test", DateTimeOffset.UtcNow);
        entity.AddAddress(EntityAddressPurpose.RegisteredOffice, "1 Supplier St", null, "Sydney", "NSW", "2000", "AU", DateTimeOffset.UtcNow);
        return entity;
    }

    private static (SupplierChangeRequestService Service, EntityDbContext Db) NewService()
    {
        var db = new EntityDbContext(new DbContextOptionsBuilder<EntityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var service = new SupplierChangeRequestService(db, new AuditRecorder(new FakeAuditStore(), TimeProvider.System), TimeProvider.System);
        return (service, db);
    }
}
