using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Workers.Notifications.Domain;

namespace ThreeCommerce.Workers.Notifications.Infrastructure;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<NotificationDelivery> Deliveries => Set<NotificationDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("notifications"); // per-service named schema (ADR-0022)

        var delivery = modelBuilder.Entity<NotificationDelivery>();
        delivery.ToTable("deliveries");
        delivery.HasKey(x => x.Id);
        delivery.Property(x => x.Channel).HasMaxLength(32);
        delivery.Property(x => x.Recipient).HasMaxLength(320);
        delivery.Property(x => x.Subject).HasMaxLength(400);
        delivery.Property(x => x.Error).HasMaxLength(1000);
        delivery.HasIndex(x => x.OccurredAt);
        delivery.HasIndex(x => x.Status);
    }
}
