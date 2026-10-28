using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;
using ThreeCommerce.Usage.Domain;

namespace ThreeCommerce.Usage.Infrastructure;

public sealed class UsageDbContext(DbContextOptions<UsageDbContext> options) : DbContext(options)
{
    public DbSet<UsageBalance> UsageBalances => Set<UsageBalance>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<UsageCharge> UsageCharges => Set<UsageCharge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("usage");
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.ConfigureJobRuns(); // scheduled usage-period-close run history (mt6_3)
        modelBuilder.ConfigureScheduleOverrides(); // operator schedule overrides (Scheduled-Job Manager)

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

        // Charges published to Payments and their reported outcome (usage_charge_failure_feedback).
        modelBuilder.Entity<UsageBalance>().Property(x => x.PaymentHold).HasConversion<string>().HasMaxLength(12)
            .HasDefaultValue(UsagePaymentHold.None); // existing balances: no hold
        modelBuilder.Entity<UsageCharge>(charge =>
        {
            charge.Property(x => x.Reference).HasMaxLength(160);
            charge.Property(x => x.Kind).HasConversion<string>().HasMaxLength(12);
            charge.Property(x => x.State).HasConversion<string>().HasMaxLength(12);
            charge.Property(x => x.LastFailureReason).HasConversion<string>().HasMaxLength(16);
            charge.Property(x => x.Currency).HasMaxLength(3);
            charge.HasIndex(x => new { x.TenantId, x.Reference }).IsUnique();
            charge.HasIndex(x => new { x.BalanceId, x.State });
        });
    }
}
