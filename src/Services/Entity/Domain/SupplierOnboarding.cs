namespace ThreeCommerce.Entity.Domain;

public sealed class SupplierOnboarding
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid EntityId { get; init; }

    /// <summary>How this supplier sources/supplies (ADR-0028). Gates which Offer kinds it can back.</summary>
    public SupplierType SupplierType { get; set; } = SupplierType.WarehousePartner;
    public SupplierOnboardingState State { get; private set; } = SupplierOnboardingState.Draft;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public string? SuspensionReason { get; private set; }

    private SupplierOnboarding()
    {
    }

    public static SupplierOnboarding Start(EntityRecord entity, DateTimeOffset now)
    {
        if (entity.Status == EntityRecordStatus.Archived)
        {
            throw new DomainRuleException("Archived entities cannot be onboarded as suppliers.");
        }

        entity.AddProfile(EntityRoleKind.Supplier, now);
        return new SupplierOnboarding
        {
            Id = Guid.CreateVersion7(),
            TenantId = entity.TenantId,
            EntityId = entity.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public SupplierReadinessResult CheckReadiness(EntityRecord entity)
    {
        var missing = new List<string>();
        if (!entity.Profiles.Any(p => p.Role == EntityRoleKind.Supplier && p.Status == EntityProfileStatus.Active))
        {
            missing.Add("supplier profile");
        }

        if (!entity.Identifiers.Any(i => i.Type is EntityIdentifierType.Abn or EntityIdentifierType.Acn && i.VerificationStatus == EntityVerificationStatus.Verified))
        {
            missing.Add("verified ABN or ACN");
        }

        if (!entity.ContactMethods.Any(c => c.Purpose is EntityContactPurpose.Primary or EntityContactPurpose.Operations && c.Kind == EntityContactKind.Email))
        {
            missing.Add("primary or operations email contact");
        }

        if (!entity.Addresses.Any(a => a.Purpose is EntityAddressPurpose.RegisteredOffice or EntityAddressPurpose.Warehouse && a.IsCurrent))
        {
            missing.Add("current registered office or warehouse address");
        }

        return new SupplierReadinessResult(missing.Count == 0, missing);
    }

    public void SubmitForVerification(EntityRecord entity, DateTimeOffset now)
    {
        EnsureState(SupplierOnboardingState.Draft);
        var readiness = CheckReadiness(entity);
        if (!readiness.IsReady)
        {
            throw new DomainRuleException($"Supplier is missing: {string.Join(", ", readiness.MissingRequirements)}.");
        }

        State = SupplierOnboardingState.PendingVerification;
        UpdatedAt = now;
    }

    public void MarkVerificationComplete(DateTimeOffset now)
    {
        EnsureState(SupplierOnboardingState.PendingVerification);
        State = SupplierOnboardingState.PendingApproval;
        UpdatedAt = now;
    }

    public void Activate(DateTimeOffset now)
    {
        EnsureState(SupplierOnboardingState.PendingApproval);
        State = SupplierOnboardingState.Active;
        ActivatedAt = now;
        SuspensionReason = null;
        UpdatedAt = now;
    }

    public void Suspend(string reason, DateTimeOffset now)
    {
        if (State != SupplierOnboardingState.Active)
        {
            throw new DomainRuleException("Only active suppliers can be suspended.");
        }

        var normalizedReason = reason.Trim();
        if (normalizedReason.Length is < 8 or > 500)
        {
            throw new DomainRuleException("Suspension reason must be between 8 and 500 characters.");
        }

        State = SupplierOnboardingState.Suspended;
        SuspensionReason = normalizedReason;
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        if (State == SupplierOnboardingState.Archived)
        {
            return;
        }

        State = SupplierOnboardingState.Archived;
        ArchivedAt = now;
        UpdatedAt = now;
    }

    private void EnsureState(SupplierOnboardingState expected)
    {
        if (State != expected)
        {
            throw new DomainRuleException($"Supplier onboarding must be {expected}; current state is {State}.");
        }
    }
}

public sealed record SupplierReadinessResult(bool IsReady, IReadOnlyList<string> MissingRequirements);

public enum SupplierOnboardingState
{
    Draft = 1,
    PendingVerification = 2,
    PendingApproval = 3,
    Active = 4,
    Suspended = 5,
    Archived = 6,
}

/// <summary>What kind of supplier this is (ADR-0028) — drives which fulfilment types it can offer.</summary>
public enum SupplierType
{
    Internal = 1,
    WarehousePartner = 2,
    DropshipPartner = 3,
    DigitalProvider = 4,
    ServiceProvider = 5,
    MarketplaceSeller = 6,
}
