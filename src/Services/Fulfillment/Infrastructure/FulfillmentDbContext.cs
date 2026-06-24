using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure;

public class FulfillmentDbContext(DbContextOptions<FulfillmentDbContext> options) : DbContext(options)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentLine> ShipmentLines => Set<ShipmentLine>();
    public DbSet<InventoryLocation> InventoryLocations => Set<InventoryLocation>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("fulfillment");

        modelBuilder.Entity<Shipment>(s =>
        {
            s.HasIndex(x => new { x.OrderId, x.FulfillmentSource }).IsUnique();
            s.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.ShipmentId);
        });

        modelBuilder.Entity<InventoryLocation>(loc =>
        {
            loc.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            loc.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            loc.Property(x => x.Name).HasMaxLength(200);
            loc.HasIndex(x => new { x.TenantId, x.EntityId });
        });

        modelBuilder.Entity<InventoryItem>(item =>
        {
            // One stock row per (tenant, location, product, variant). VariantId is nullable, so
            // uniqueness is enforced in InventoryService (Postgres treats NULLs as distinct).
            item.HasIndex(x => new { x.TenantId, x.LocationId, x.ProductId, x.VariantId });
            item.HasIndex(x => new { x.TenantId, x.ProductId, x.VariantId });
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
