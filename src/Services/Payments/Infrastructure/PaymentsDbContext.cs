using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Ledger;
using ThreeCommerce.Payments.Domain.Xero;

namespace ThreeCommerce.Payments.Infrastructure;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<WebhookInboxEntry> WebhookInbox => Set<WebhookInboxEntry>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("payments");

        modelBuilder.Entity<LedgerAccount>().HasKey(a => a.Code);

        modelBuilder.Entity<JournalEntry>(e =>
        {
            e.HasIndex(x => x.Reference);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.EntryId);
        });

        modelBuilder.Entity<JournalLine>(l =>
        {
            l.HasIndex(x => x.AccountCode);
            l.ToTable(t =>
            {
                t.HasCheckConstraint("ck_line_nonneg", "\"DebitMinor\" >= 0 AND \"CreditMinor\" >= 0");
                t.HasCheckConstraint("ck_line_one_side", "(\"DebitMinor\" = 0) <> (\"CreditMinor\" = 0)");
            });
        });

        modelBuilder.Entity<Payment>(p =>
        {
            p.HasIndex(x => x.OrderId).IsUnique();
            p.HasIndex(x => x.PaymentIntentId);
            p.Property(x => x.Currency).HasMaxLength(3);
        });

        modelBuilder.Entity<Refund>(r => r.HasIndex(x => x.OrderId));

        modelBuilder.Entity<WebhookInboxEntry>().HasKey(x => x.EventId);
        modelBuilder.Entity<IdempotencyRecord>().HasKey(x => x.Key);
        modelBuilder.Entity<SyncRun>(s =>
        {
            s.HasKey(x => x.Id);
            s.HasIndex(x => x.Reference).IsUnique();
        });

        // MassTransit transactional outbox + inbox tables (ADR-0007).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
