namespace ThreeCommerce.Workflow.Domain;

/// <summary>
/// A projected scheduled-job run (mt6_3 central workflow visibility). Services run their own jobs (the
/// scheduling primitive lives in BuildingBlocks); this is the cross-service run-history read model.
/// </summary>
public sealed class WorkflowRun
{
    public Guid Id { get; init; }
    public required string JobName { get; init; }
    public required string Status { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}
