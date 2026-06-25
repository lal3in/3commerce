using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ThreeCommerce.Workflow.Infrastructure;

namespace ThreeCommerce.Workflow.Api;

public sealed class WorkflowDbContextDesignFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    public WorkflowDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONS_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=workflow_db;Username=workflow_svc;Password=workflow_dev";
        return new WorkflowDbContext(new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")).Options);
    }
}
