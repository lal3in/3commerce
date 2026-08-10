namespace ThreeCommerce.Workflow.Domain;

/// <summary>
/// The central registry of a scheduled job's current schedule/state (Scheduled-Job Manager read model),
/// projected from each service's <c>ScheduledJobDescriptor</c>. Keyed by (Service, Name). Merged with the
/// latest <see cref="WorkflowRun"/> to render the cross-service manager in Mission Control.
/// </summary>
public sealed class ScheduledJobEntry
{
    public required string Service { get; init; }
    public required string Name { get; init; }
    public string Cron { get; set; } = string.Empty;
    public bool Paused { get; set; }
    public DateTimeOffset? NextFireUtc { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
