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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Ticket>(t =>
        {
            t.HasIndex(x => x.OrderId);
            t.HasMany(x => x.Messages).WithOne().HasForeignKey(m => m.TicketId);
        });

        modelBuilder.Entity<RmaState>(s =>
        {
            s.HasKey(x => x.CorrelationId);
            s.Property(x => x.CurrentState).HasMaxLength(64);
            s.HasIndex(x => x.OrderId);
            s.HasIndex(x => x.RefundId);
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
