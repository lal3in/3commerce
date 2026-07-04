using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Marketing.Domain;

namespace ThreeCommerce.Marketing.Infrastructure;

public sealed class MarketingDbContext(DbContextOptions<MarketingDbContext> options) : DbContext(options)
{
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<ShortLink> ShortLinks => Set<ShortLink>();
    public DbSet<AnalyticsEventRecord> AnalyticsEvents => Set<AnalyticsEventRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("marketing");

        // MassTransit EF outbox/inbox tables (the bus uses UseEntityFrameworkOutbox<MarketingDbContext>).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<Campaign>(campaign =>
        {
            campaign.HasKey(c => c.Id);
            campaign.Property(c => c.Cid).HasMaxLength(64);
            campaign.Property(c => c.Name).HasMaxLength(200);
            campaign.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            campaign.HasIndex(c => new { c.TenantId, c.Cid }).IsUnique();
        });

        // Append-only event store (def_4): dedupe key is the client-generated event id per tenant.
        modelBuilder.Entity<AnalyticsEventRecord>(ev =>
        {
            ev.Property(e => e.EventType).HasMaxLength(128);
            ev.Property(e => e.EventId).HasMaxLength(64);
            ev.Property(e => e.VisitorId).HasMaxLength(64);
            ev.Property(e => e.SessionId).HasMaxLength(64);
            ev.Property(e => e.ClientIpCoarse).HasMaxLength(64);
            ev.Property(e => e.PayloadJson).HasColumnType("jsonb");
            ev.HasIndex(e => new { e.TenantId, e.EventId }).IsUnique();
            ev.HasIndex(e => new { e.TenantId, e.ReceivedAt });
        });

        modelBuilder.Entity<ShortLink>(link =>
        {
            link.HasKey(l => l.Id);
            link.Property(l => l.Code).HasMaxLength(16);
            link.Property(l => l.Destination).HasMaxLength(2048);
            link.Property(l => l.Cid).HasMaxLength(64);
            link.Property(l => l.Status).HasConversion<string>().HasMaxLength(16);
            link.HasIndex(l => new { l.TenantId, l.Code }).IsUnique();
        });
    }
}
