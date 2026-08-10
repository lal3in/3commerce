using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;
using ThreeCommerce.Workflow.Domain;

namespace ThreeCommerce.Workflow.Infrastructure;

public sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowRun> Runs => Set<WorkflowRun>();
    public DbSet<ScheduledJobEntry> ScheduledJobs => Set<ScheduledJobEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("workflow");
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.ConfigureScheduleOverrides(); // uniform across services (unused here — Workflow runs no jobs)

        modelBuilder.Entity<WorkflowRun>(run =>
        {
            run.HasKey(r => r.Id);
            run.Property(r => r.JobName).HasMaxLength(128);
            run.Property(r => r.Service).HasMaxLength(64);
            run.Property(r => r.Status).HasMaxLength(16);
            run.Property(r => r.Error).HasMaxLength(1024);
            run.HasIndex(r => new { r.JobName, r.StartedAt });
        });

        modelBuilder.Entity<ScheduledJobEntry>(job =>
        {
            job.HasKey(j => new { j.Service, j.Name });
            job.Property(j => j.Service).HasMaxLength(64);
            job.Property(j => j.Name).HasMaxLength(128);
            job.Property(j => j.Cron).HasMaxLength(128);
        });
    }
}
