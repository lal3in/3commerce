using ThreeCommerce.Workflow.Domain;

namespace ThreeCommerce.Workflow.Tests;

public class WorkflowRunTests
{
    [Fact]
    public void Run_projection_holds_status_and_timing()
    {
        var run = new WorkflowRun { Id = Guid.NewGuid(), JobName = "daily-journal", Status = "Succeeded", StartedAt = DateTimeOffset.UnixEpoch };
        Assert.Equal("daily-journal", run.JobName);
        Assert.Equal("Succeeded", run.Status);
    }

}
