namespace ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

public sealed class QuartzSchedulerOptions
{
    public const string SectionName = "Quartz";

    public bool PersistentStoreEnabled { get; init; }

    public string? ConnectionString { get; init; }

    public string TablePrefix { get; init; } = "QRTZ_";

    public string SchedulerName { get; init; } = "3commerce-scheduler";

    public string InstanceId { get; init; } = "AUTO";

    public int MisfireThresholdSeconds { get; init; } = 60;

    public int ClusterCheckinIntervalSeconds { get; init; } = 10;

    public int ClusterCheckinMisfireThresholdSeconds { get; init; } = 60;
}
