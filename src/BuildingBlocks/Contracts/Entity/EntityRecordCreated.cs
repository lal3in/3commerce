namespace ThreeCommerce.BuildingBlocks.Contracts.Entity;

public sealed record EntityRecordCreated(
    Guid EntityId,
    Guid TenantId,
    string DisplayName,
    DateTimeOffset OccurredAt);
