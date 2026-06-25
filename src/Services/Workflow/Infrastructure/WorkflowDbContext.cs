using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Workflow.Domain;

namespace ThreeCommerce.Workflow.Infrastructure;

public sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowRun> Runs => Set<WorkflowRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("workflow");
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<WorkflowRun>(run =>
        {
            run.HasKey(r => r.Id);
            run.Property(r => r.JobName).HasMaxLength(128);
            run.Property(r => r.Status).HasMaxLength(16);
            run.Property(r => r.Error).HasMaxLength(1024);
            run.HasIndex(r => new { r.JobName, r.StartedAt });
        });
    }
}
