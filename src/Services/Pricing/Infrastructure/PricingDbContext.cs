using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Pricing.Domain;

namespace ThreeCommerce.Pricing.Infrastructure;

public sealed class PricingDbContext(DbContextOptions<PricingDbContext> options) : DbContext(options)
{
    public DbSet<Price> Prices => Set<Price>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("pricing");
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<Price>(price =>
        {
            price.HasKey(p => p.Id);
            price.Property(p => p.PricingModel).HasConversion<string>().HasMaxLength(24);
            price.Property(p => p.BillingPeriod).HasConversion<string>().HasMaxLength(12);
            price.Property(p => p.Currency).HasMaxLength(3);
            price.HasMany(p => p.Tiers).WithOne().HasForeignKey(t => t.PriceId);
            price.HasIndex(p => new { p.TenantId, p.ProductId, p.VariantId });
        });

        modelBuilder.Entity<PriceTier>(tier => tier.HasKey(t => t.Id));
    }
}
