using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Support.Domain;
using ThreeCommerce.Support.Infrastructure.Sagas;

namespace ThreeCommerce.Support.Infrastructure;

public class SupportDbContext(DbContextOptions<SupportDbContext> options) : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();
    public DbSet<RmaState> Rmas => Set<RmaState>();
    public DbSet<OrderSnapshot> OrderSnapshots => Set<OrderSnapshot>();
    public DbSet<OrderSnapshotLine> OrderSnapshotLines => Set<OrderSnapshotLine>();
    public DbSet<SupportAttachment> Attachments => Set<SupportAttachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("support");

        modelBuilder.Entity<Ticket>(t =>
        {
            t.HasIndex(x => x.OrderId);
            t.HasMany(x => x.Messages).WithOne().HasForeignKey(m => m.TicketId);
        });

        modelBuilder.Entity<SupportAttachment>(a =>
        {
            a.Property(x => x.OwnerKind).HasMaxLength(16);
            a.Property(x => x.FileName).HasMaxLength(260);
            a.Property(x => x.ContentType).HasMaxLength(100);
            a.Property(x => x.StorageKey).HasMaxLength(512);
            a.HasIndex(x => new { x.OwnerKind, x.OwnerId });
        });

        modelBuilder.Entity<RmaState>(s =>
        {
            s.HasKey(x => x.CorrelationId);
            s.Property(x => x.CurrentState).HasMaxLength(64);
            s.HasIndex(x => x.OrderId);
            s.HasIndex(x => x.RefundId);
        });

        modelBuilder.Entity<OrderSnapshot>(o =>
        {
            o.HasKey(x => x.OrderId);
            o.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId);
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
