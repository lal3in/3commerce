using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Entity.Domain;
using ThreeCommerce.Entity.Infrastructure;

namespace ThreeCommerce.Entity.Tests;

public class SupplierOnboardingTests
{
    [Fact]
    public void SupplierOnboarding_starts_in_draft_and_adds_supplier_profile()
    {
        var entity = NewReadySupplier();

        var onboarding = SupplierOnboarding.Start(entity, DateTimeOffset.UtcNow);

        Assert.Equal(SupplierOnboardingState.Draft, onboarding.State);
        Assert.Contains(entity.Profiles, p => p.Role == EntityRoleKind.Supplier);
    }

    [Fact]
    public void SupplierOnboarding_readiness_reports_missing_requirements()
    {
        var entity = EntityRecord.Create(Guid.CreateVersion7(), EntityType.Company, "Acme Pty Ltd", null, DateTimeOffset.UtcNow, []);
        var onboarding = SupplierOnboarding.Start(entity, DateTimeOffset.UtcNow);

        var readiness = onboarding.CheckReadiness(entity);

        Assert.False(readiness.IsReady);
        Assert.Contains("verified ABN or ACN", readiness.MissingRequirements);
        Assert.Contains("primary or operations email contact", readiness.MissingRequirements);
        Assert.Contains("current registered office or warehouse address", readiness.MissingRequirements);
    }

    [Fact]
    public void SupplierOnboarding_progresses_draft_to_active()
    {
        var entity = NewReadySupplier();
        var onboarding = SupplierOnboarding.Start(entity, DateTimeOffset.UtcNow);

        onboarding.SubmitForVerification(entity, DateTimeOffset.UtcNow.AddMinutes(1));
        onboarding.MarkVerificationComplete(DateTimeOffset.UtcNow.AddMinutes(2));
        onboarding.Activate(DateTimeOffset.UtcNow.AddMinutes(3));

        Assert.Equal(SupplierOnboardingState.Active, onboarding.State);
        Assert.NotNull(onboarding.ActivatedAt);
    }

    [Fact]
    public void SupplierOnboarding_prevents_activation_before_approval()
    {
        var onboarding = SupplierOnboarding.Start(NewReadySupplier(), DateTimeOffset.UtcNow);

        Assert.Throws<DomainRuleException>(() => onboarding.Activate(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SupplierOnboarding_suspends_only_active_supplier()
    {
        var entity = NewReadySupplier();
        var onboarding = SupplierOnboarding.Start(entity, DateTimeOffset.UtcNow);
        onboarding.SubmitForVerification(entity, DateTimeOffset.UtcNow);
        onboarding.MarkVerificationComplete(DateTimeOffset.UtcNow);
        onboarding.Activate(DateTimeOffset.UtcNow);

        onboarding.Suspend("Temporary compliance issue", DateTimeOffset.UtcNow);

        Assert.Equal(SupplierOnboardingState.Suspended, onboarding.State);
        Assert.Equal("Temporary compliance issue", onboarding.SuspensionReason);
    }

    [Fact]
    public async Task SupplierOnboarding_service_is_idempotent_for_start()
    {
        await using var db = NewDb();
        var entity = NewReadySupplier();
        db.Entities.Add(entity);
        await db.SaveChangesAsync();
        var service = new SupplierOnboardingService(db, TimeProvider.System);

        var first = await service.StartAsync(entity.Id, default);
        var second = await service.StartAsync(entity.Id, default);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await db.SupplierOnboardings.ToListAsync());
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

    private static EntityDbContext NewDb() => new(new DbContextOptionsBuilder<EntityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);
}
