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

    /// <summary>
    /// How a cron trigger recovers a fire it missed (e.g. the service was down past the misfire threshold):
    /// <c>DoNothing</c> skips the missed fire and resumes on the next scheduled tick (default — right for
    /// idempotent recurring jobs); <c>FireAndProceed</c> runs the missed fire once immediately then resumes;
    /// <c>Ignore</c> reapplies all missed fires. Tune per job cadence in production.
    /// </summary>
    public string MisfirePolicy { get; init; } = "DoNothing";

    /// <summary>Extra attempts after the first failure before a run is recorded Failed (0 = no retry).</summary>
    public int MaxJobRetries { get; init; }

    /// <summary>Delay between retry attempts.</summary>
    public int RetryDelaySeconds { get; init; } = 5;

    public int ClusterCheckinIntervalSeconds { get; init; } = 10;

    public int ClusterCheckinMisfireThresholdSeconds { get; init; } = 60;
}
