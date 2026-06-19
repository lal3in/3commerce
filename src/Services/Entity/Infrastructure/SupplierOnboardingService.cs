using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Entity.Domain;

namespace ThreeCommerce.Entity.Infrastructure;

public sealed class SupplierOnboardingService(EntityDbContext db, TimeProvider timeProvider)
{
    public async Task<SupplierOnboarding> StartAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var existing = await db.SupplierOnboardings.SingleOrDefaultAsync(s => s.EntityId == entityId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var entity = await LoadEntityAsync(entityId, cancellationToken);
        var hadSupplierProfile = entity.Profiles.Any(p => p.Role == EntityRoleKind.Supplier && p.Status == EntityProfileStatus.Active);
        var onboarding = SupplierOnboarding.Start(entity, timeProvider.GetUtcNow());
        if (!hadSupplierProfile)
        {
            db.Entry(entity.Profiles.Single(p => p.Role == EntityRoleKind.Supplier && p.Status == EntityProfileStatus.Active)).State = EntityState.Added;
        }

        db.SupplierOnboardings.Add(onboarding);
        await db.SaveChangesAsync(cancellationToken);
        return onboarding;
    }

    public async Task<SupplierReadinessResult> CheckReadinessAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var entity = await LoadEntityAsync(entityId, cancellationToken);
        var onboarding = await GetOrCreateDraftAsync(entity, cancellationToken);
        return onboarding.CheckReadiness(entity);
    }

    public async Task<SupplierOnboarding> SubmitForVerificationAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var entity = await LoadEntityAsync(entityId, cancellationToken);
        var onboarding = await GetOrCreateDraftAsync(entity, cancellationToken);
        onboarding.SubmitForVerification(entity, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return onboarding;
    }

    public async Task<SupplierOnboarding> MarkVerificationCompleteAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var onboarding = await db.SupplierOnboardings.SingleAsync(s => s.EntityId == entityId, cancellationToken);
        onboarding.MarkVerificationComplete(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return onboarding;
    }

    public async Task<SupplierOnboarding> ActivateAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var onboarding = await db.SupplierOnboardings.SingleAsync(s => s.EntityId == entityId, cancellationToken);
        onboarding.Activate(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return onboarding;
    }

    public async Task<SupplierOnboarding> SuspendAsync(Guid entityId, string reason, CancellationToken cancellationToken)
    {
        var onboarding = await db.SupplierOnboardings.SingleAsync(s => s.EntityId == entityId, cancellationToken);
        onboarding.Suspend(reason, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return onboarding;
    }

    public async Task<SupplierOnboarding> ArchiveAsync(Guid entityId, CancellationToken cancellationToken)
    {
        var onboarding = await db.SupplierOnboardings.SingleAsync(s => s.EntityId == entityId, cancellationToken);
        onboarding.Archive(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return onboarding;
    }

    private async Task<SupplierOnboarding> GetOrCreateDraftAsync(EntityRecord entity, CancellationToken cancellationToken)
    {
        var onboarding = await db.SupplierOnboardings.SingleOrDefaultAsync(s => s.EntityId == entity.Id, cancellationToken);
        if (onboarding is not null)
        {
            return onboarding;
        }

        var hadSupplierProfile = entity.Profiles.Any(p => p.Role == EntityRoleKind.Supplier && p.Status == EntityProfileStatus.Active);
        onboarding = SupplierOnboarding.Start(entity, timeProvider.GetUtcNow());
        if (!hadSupplierProfile)
        {
            db.Entry(entity.Profiles.Single(p => p.Role == EntityRoleKind.Supplier && p.Status == EntityProfileStatus.Active)).State = EntityState.Added;
        }

        db.SupplierOnboardings.Add(onboarding);
        return onboarding;
    }

    private async Task<EntityRecord> LoadEntityAsync(Guid entityId, CancellationToken cancellationToken) =>
        await db.Entities
            .Include(e => e.Profiles)
            .Include(e => e.Identifiers)
            .Include(e => e.ContactMethods)
            .Include(e => e.Addresses)
            .SingleAsync(e => e.Id == entityId, cancellationToken);
}
