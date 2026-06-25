using Microsoft.EntityFrameworkCore;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Audit;

public static class AuditModelExtensions
{
    /// <summary>Map the local audit table the same way in every service (mt6_1). Uses the model's default schema.</summary>
    public static ModelBuilder ConfigureAudit(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEntry>(entry =>
        {
            entry.ToTable("AuditEntries");
            entry.HasKey(x => x.Id);
            entry.Property(x => x.Action).HasMaxLength(128);
            entry.Property(x => x.ResourceType).HasMaxLength(64);
            entry.Property(x => x.ResourceId).HasMaxLength(128);
            entry.Property(x => x.ActorRole).HasMaxLength(64);
            entry.Property(x => x.Summary).HasMaxLength(512);
            entry.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
            entry.Property(x => x.PrevHash).HasMaxLength(80);
            entry.Property(x => x.Hash).HasMaxLength(80);

            // Per-tenant append-only ordering: a sequence appears once.
            entry.HasIndex(x => new { x.TenantId, x.Sequence }).IsUnique();
        });

        return modelBuilder;
    }
}
