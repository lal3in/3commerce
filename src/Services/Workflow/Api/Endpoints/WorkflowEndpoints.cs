using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Workflow.Domain;
using ThreeCommerce.Workflow.Infrastructure;

namespace ThreeCommerce.Workflow.Api.Endpoints;

/// <summary>Central scheduled-job run history (mt6_3), read-only.</summary>
public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowRuns(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/workflow/runs", async (string? job, WorkflowDbContext db, CancellationToken ct) =>
            {
                var query = db.Runs.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(job)) query = query.Where(r => r.JobName == job);
                var runs = await query.OrderByDescending(r => r.StartedAt).Take(200).ToListAsync(ct);
                return TypedResults.Ok(runs.Select(ToDto).ToList());
            })
            .WithTags("Workflow")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);
        return app;
    }

    private static WorkflowRunDto ToDto(WorkflowRun r) => new(r.Id, r.JobName, r.Status, r.StartedAt, r.CompletedAt, r.Error);
}

public record WorkflowRunDto(Guid Id, string JobName, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error);
