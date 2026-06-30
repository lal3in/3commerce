namespace ThreeCommerce.BuildingBlocks.Contracts.Streams;

public static class StreamPartitionKeys
{
    public static string TenantAggregate(Guid tenantId, Guid aggregateId) => $"{tenantId}:{aggregateId}";

    public static string TenantAggregate(Guid tenantId, string aggregateId)
    {
        if (string.IsNullOrWhiteSpace(aggregateId))
            throw new ArgumentException("Aggregate id is required.", nameof(aggregateId));

        return $"{tenantId}:{aggregateId.Trim()}";
    }

    public static string Tenant(Guid tenantId) => tenantId.ToString();
}
