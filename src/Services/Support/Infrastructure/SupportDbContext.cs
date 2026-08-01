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
    public DbSet<RmaRequestRecord> RmaRequests => Set<RmaRequestRecord>();
    public DbSet<RmaRequestLine> RmaRequestLines => Set<RmaRequestLine>();
    public DbSet<RmaDisposition> RmaDispositions => Set<RmaDisposition>();

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

        modelBuilder.Entity<RmaRequestRecord>(r =>
        {
            r.Property(x => x.Email).HasMaxLength(320);
            r.Property(x => x.Reason).HasMaxLength(1000);
            r.Property(x => x.Currency).HasMaxLength(3);
            r.HasIndex(x => x.OrderId);
            r.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.RmaId);
        });

        modelBuilder.Entity<RmaRequestLine>(l =>
        {
            l.Property(x => x.Title).HasMaxLength(300);
            l.HasIndex(x => x.RmaId);
        });

        modelBuilder.Entity<RmaDisposition>(d =>
        {
            d.HasKey(x => x.RmaId);
            d.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            d.Property(x => x.StorageReason).HasConversion<string>().HasMaxLength(16);
            d.Property(x => x.Comments).HasMaxLength(2000);
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
