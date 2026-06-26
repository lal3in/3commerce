using MassTransit;
using Microsoft.EntityFrameworkCore;
using EntitlementRecord = ThreeCommerce.Entitlement.Domain.Entitlement;

namespace ThreeCommerce.Entitlement.Infrastructure;

public sealed class EntitlementDbContext(DbContextOptions<EntitlementDbContext> options) : DbContext(options)
{
    public DbSet<EntitlementRecord> Entitlements => Set<EntitlementRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("entitlement");
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<EntitlementRecord>(entitlement =>
        {
            entitlement.HasKey(x => x.Id);
            entitlement.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            entitlement.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            entitlement.Property(x => x.CustomerEmail).HasMaxLength(256);
            entitlement.HasIndex(x => new { x.TenantId, x.CustomerEmail });
            entitlement.HasIndex(x => new { x.TenantId, x.OrderId });
        });
    }
}
