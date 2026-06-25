using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Fulfillment.Domain;

namespace ThreeCommerce.Fulfillment.Infrastructure;

public class FulfillmentDbContext(DbContextOptions<FulfillmentDbContext> options) : DbContext(options)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentLine> ShipmentLines => Set<ShipmentLine>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<InventoryLocation> InventoryLocations => Set<InventoryLocation>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<CarrierIntegration> CarrierIntegrations => Set<CarrierIntegration>();
    public DbSet<Domain.Dropship.SupplierOrder> SupplierOrders => Set<Domain.Dropship.SupplierOrder>();
    public DbSet<Domain.Dropship.SupplierAvailability> SupplierAvailabilities => Set<Domain.Dropship.SupplierAvailability>();
    public DbSet<OrderHold> OrderHolds => Set<OrderHold>();
    public DbSet<HeldOrder> HeldOrders => Set<HeldOrder>();
    public DbSet<Entitlement> Entitlements => Set<Entitlement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("fulfillment");

        modelBuilder.Entity<Shipment>(s =>
        {
            s.HasIndex(x => new { x.OrderId, x.FulfillmentSource }).IsUnique();
            s.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.ShipmentId);
            s.HasMany(x => x.Packages).WithOne().HasForeignKey(p => p.ShipmentId);
        });

        modelBuilder.Entity<Package>(p =>
        {
            p.Property(x => x.Carrier).HasConversion<string>().HasMaxLength(24);
            p.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            p.HasIndex(x => x.ShipmentId);
            p.HasIndex(x => new { x.TenantId, x.TrackingNumber });
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

        modelBuilder.Entity<InventoryMovement>(move =>
        {
            move.Property(x => x.Type).HasConversion<string>().HasMaxLength(24);
            move.Property(x => x.ReferenceType).HasConversion<string>().HasMaxLength(24);
            // Idempotency + history lookups are by reference (order) and type.
            move.HasIndex(x => new { x.ReferenceId, x.Type });
            move.HasIndex(x => x.InventoryItemId);
        });

        modelBuilder.Entity<CarrierIntegration>(carrier =>
        {
            carrier.Property(x => x.Carrier).HasConversion<string>().HasMaxLength(24);
            carrier.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            carrier.Property(x => x.CredentialRef).HasMaxLength(200);
            carrier.HasIndex(x => new { x.TenantId, x.StorefrontId });
        });

        modelBuilder.Entity<Domain.Dropship.SupplierOrder>(order =>
        {
            order.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            order.HasIndex(x => new { x.OrderId, x.SupplierId }).IsUnique();
            order.HasIndex(x => new { x.TenantId, x.OrderId });
        });

        modelBuilder.Entity<Domain.Dropship.SupplierAvailability>(availability =>
        {
            availability.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            availability.Property(x => x.SupplierSku).HasMaxLength(100);
            availability.HasIndex(x => new { x.TenantId, x.SupplierId, x.ProductId, x.VariantId });
        });

        modelBuilder.Entity<OrderHold>(hold =>
        {
            hold.Property(x => x.Reason).HasConversion<string>().HasMaxLength(16);
            hold.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            hold.HasIndex(x => new { x.TenantId, x.OrderId, x.Status });
        });

        modelBuilder.Entity<HeldOrder>(held =>
        {
            held.Property(x => x.PayloadJson).HasColumnType("jsonb");
            held.HasIndex(x => x.OrderId).IsUnique();
        });

        modelBuilder.Entity<Entitlement>(entitlement =>
        {
            entitlement.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            entitlement.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            entitlement.Property(x => x.CustomerEmail).HasMaxLength(256);
            entitlement.HasIndex(x => new { x.TenantId, x.CustomerEmail });
            entitlement.HasIndex(x => new { x.TenantId, x.OrderId });
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
