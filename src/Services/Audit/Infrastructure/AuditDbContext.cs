using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Audit.Domain;

namespace ThreeCommerce.Audit.Infrastructure;

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditProjection> AuditEntries => Set<AuditProjection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("audit");
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<AuditProjection>(entry =>
        {
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Action).HasMaxLength(128);
            entry.Property(e => e.ResourceType).HasMaxLength(64);
            entry.Property(e => e.ResourceId).HasMaxLength(128);
            entry.Property(e => e.ActorRole).HasMaxLength(64);
            entry.Property(e => e.Outcome).HasMaxLength(16);
            entry.Property(e => e.Summary).HasMaxLength(512);
            entry.Property(e => e.Hash).HasMaxLength(80);
            // Idempotency key: the entry's content hash. NOT (TenantId, Sequence) — sequence is the
            // producer's local chain position, which restarts per service (and is always 1 for
            // publish-only producers), so distinct entries from different services would collide.
            entry.HasIndex(e => new { e.TenantId, e.Hash }).IsUnique();
            entry.HasIndex(e => new { e.TenantId, e.ResourceType, e.ResourceId });
        });
    }
}
