namespace ThreeCommerce.Entity.Domain;

public sealed class DuplicateWarning
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public Guid CandidateEntityId { get; init; }
    public Guid ExistingEntityId { get; init; }
    public DuplicateWarningKind Kind { get; init; }
    public required string MatchedValue { get; init; }
    public DuplicateWarningStatus Status { get; private set; } = DuplicateWarningStatus.Open;
    public string? OverrideReason { get; private set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? OverriddenAt { get; private set; }

    private DuplicateWarning()
    {
    }

    public static DuplicateWarning Create(
        Guid tenantId,
        Guid candidateEntityId,
        Guid existingEntityId,
        DuplicateWarningKind kind,
        string matchedValue,
        DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || candidateEntityId == Guid.Empty || existingEntityId == Guid.Empty)
        {
            throw new DomainRuleException("Duplicate warning tenant and entity IDs are required.");
        }

        if (candidateEntityId == existingEntityId)
        {
            throw new DomainRuleException("Duplicate warning requires two distinct entities.");
        }

        var value = matchedValue.Trim();
        if (value.Length is < 2 or > 320)
        {
            throw new DomainRuleException("MatchedValue must be between 2 and 320 characters.");
        }

        return new DuplicateWarning
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            CandidateEntityId = candidateEntityId,
            ExistingEntityId = existingEntityId,
            Kind = kind,
            MatchedValue = value,
            CreatedAt = now,
        };
    }

    public void Override(string reason, DateTimeOffset now)
    {
        var normalizedReason = reason.Trim();
        if (normalizedReason.Length is < 8 or > 500)
        {
            throw new DomainRuleException("Override reason must be between 8 and 500 characters.");
        }

        Status = DuplicateWarningStatus.Overridden;
        OverrideReason = normalizedReason;
        OverriddenAt = now;
    }
}

public enum DuplicateWarningKind
{
    LegalName = 1,
    TradingName = 2,
    Identifier = 3,
    Contact = 4,
}

public enum DuplicateWarningStatus
{
    Open = 1,
    Overridden = 2,
}
