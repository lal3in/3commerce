namespace ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;

/// <summary>A scheduled job run finished (mt6_3). Published by JobExecutor (when a publisher is wired) so
/// the central Workflow service can show cross-service run history. Co-located with the scheduler.</summary>
public record JobRunRecorded(
    Guid Id, string JobName, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error);
