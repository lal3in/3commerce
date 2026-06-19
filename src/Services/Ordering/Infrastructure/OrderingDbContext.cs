using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure.Sagas;

namespace ThreeCommerce.Ordering.Infrastructure;

public class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<ProductCopy> ProductCopies => Set<ProductCopy>();
    public DbSet<ProductVariantCopy> ProductVariantCopies => Set<ProductVariantCopy>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<CheckoutAttempt> CheckoutAttempts => Set<CheckoutAttempt>();
    public DbSet<CheckoutAttemptLine> CheckoutAttemptLines => Set<CheckoutAttemptLine>();
    public DbSet<OrderNumberSequence> OrderNumberSequences => Set<OrderNumberSequence>();
    public DbSet<CheckoutState> CheckoutStates => Set<CheckoutState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("ordering");

        modelBuilder.Entity<ProductCopy>(product =>
        {
            product.HasKey(p => p.ProductId);
            product.HasIndex(p => p.Slug);
            product.HasMany(p => p.Variants).WithOne().HasForeignKey(v => v.ProductId);
        });

        modelBuilder.Entity<ProductVariantCopy>(variant =>
        {
            variant.HasKey(v => v.VariantId);
            variant.HasIndex(v => new { v.ProductId, v.Sku });
            variant.Property(v => v.Currency).HasMaxLength(3);
        });

        modelBuilder.Entity<Cart>(c =>
        {
            c.HasIndex(x => x.CartKey);
            c.HasIndex(x => x.UserId);
            c.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.CartId);
            c.Navigation(x => x.Items).AutoInclude();
        });

        modelBuilder.Entity<Order>(o =>
        {
            o.Property(x => x.Currency).HasMaxLength(3);
            o.HasIndex(x => new { x.StorefrontId, x.PublicOrderNumber }).IsUnique();
            o.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId);
        });

        modelBuilder.Entity<CheckoutAttempt>(attempt =>
        {
            attempt.Property(x => x.Currency).HasMaxLength(3);
            attempt.Property(x => x.PaymentIntentId).HasMaxLength(200);
            attempt.Property(x => x.CampaignRef).HasMaxLength(120);
            attempt.HasIndex(x => new { x.StorefrontId, x.Status });
            attempt.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.CheckoutAttemptId);
        });

        modelBuilder.Entity<OrderNumberSequence>(sequence =>
        {
            sequence.HasKey(x => x.StorefrontId);
        });

        // Saga state persistence (MassTransit EF saga repository).
        modelBuilder.Entity<CheckoutState>(s =>
        {
            s.HasKey(x => x.CorrelationId);
            s.Property(x => x.CurrentState).HasMaxLength(64);
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
