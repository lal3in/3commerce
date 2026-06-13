using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure.Sagas;

namespace ThreeCommerce.Ordering.Infrastructure;

public class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<ProductCopy> ProductCopies => Set<ProductCopy>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<CheckoutState> CheckoutStates => Set<CheckoutState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ProductCopy>().HasKey(p => p.ProductId);
        modelBuilder.Entity<ProductCopy>().HasIndex(p => p.Slug);

        modelBuilder.Entity<Cart>(c =>
        {
            c.HasIndex(x => x.CartKey);
            c.HasIndex(x => x.UserId);
            c.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.CartId);
        });

        modelBuilder.Entity<Order>(o =>
        {
            o.Property(x => x.Currency).HasMaxLength(3);
            o.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId);
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
