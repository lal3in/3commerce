using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Usage.Domain;

namespace ThreeCommerce.Usage.Infrastructure;

public sealed class UsageDbContext(DbContextOptions<UsageDbContext> options) : DbContext(options)
{
    public DbSet<UsageBalance> UsageBalances => Set<UsageBalance>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("usage");
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();

        modelBuilder.Entity<UsageBalance>(balance =>
        {
            balance.Property(x => x.Meter).HasConversion<string>().HasMaxLength(16);
            balance.Property(x => x.CustomerEmail).HasMaxLength(256);
            balance.Property(x => x.Currency).HasMaxLength(3);
            balance.HasIndex(x => new { x.TenantId, x.CustomerEmail, x.Meter }).IsUnique();
        });

        modelBuilder.Entity<UsageRecord>(record =>
        {
            record.Property(x => x.Meter).HasConversion<string>().HasMaxLength(16);
            record.Property(x => x.CustomerEmail).HasMaxLength(256);
            record.HasIndex(x => new { x.TenantId, x.ReferenceId });
            record.HasIndex(x => x.BalanceId);
        });
    }
}
