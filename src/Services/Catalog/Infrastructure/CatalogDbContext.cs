using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Infrastructure;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Variant> Variants => Set<Variant>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<PingRecord> Pings => Set<PingRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Product>(product =>
        {
            product.HasIndex(p => p.Slug).IsUnique();
            product.Property(p => p.Attributes).HasColumnType("jsonb");
            product.Property(p => p.ImageUrls).HasColumnType("jsonb");
            product.HasMany(p => p.Variants).WithOne().HasForeignKey(v => v.ProductId);
            product.HasIndex(p => p.CategoryId);
            // search_vector (weighted tsvector) + GIN indexes are raw SQL in the
            // SearchSchema migration — deliberately not part of the EF model.
        });

        modelBuilder.Entity<Variant>(variant =>
        {
            variant.HasIndex(v => v.Sku).IsUnique();
            variant.Property(v => v.Currency).HasMaxLength(3);
        });

        modelBuilder.Entity<Category>(category =>
        {
            category.HasIndex(c => c.Slug).IsUnique();
        });

        modelBuilder.Entity<ImportRun>(run =>
        {
            run.Property(r => r.SampleRejections).HasColumnType("jsonb");
        });

        // MassTransit transactional outbox + inbox tables (ADR-0007).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
