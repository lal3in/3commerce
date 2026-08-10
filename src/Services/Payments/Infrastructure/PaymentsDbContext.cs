using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;
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
    public DbSet<VoidPayment> VoidPayments => Set<VoidPayment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<WebhookInboxEntry> WebhookInbox => Set<WebhookInboxEntry>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<PaymentAccount> PaymentAccounts => Set<PaymentAccount>();
    public DbSet<SupplierBankAccount> SupplierBankAccounts => Set<SupplierBankAccount>();
    public DbSet<PayoutInstruction> PayoutInstructions => Set<PayoutInstruction>();
    public DbSet<SupplierPayablePolicy> SupplierPayablePolicies => Set<SupplierPayablePolicy>();
    public DbSet<SupplierPayable> SupplierPayables => Set<SupplierPayable>();
    public DbSet<XeroAccountMapping> XeroAccountMappings => Set<XeroAccountMapping>();
    public DbSet<PaymentCustomer> PaymentCustomers => Set<PaymentCustomer>();
    public DbSet<SavedPaymentMethod> SavedPaymentMethods => Set<SavedPaymentMethod>();
    public DbSet<Mandate> Mandates => Set<Mandate>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionRenewal> SubscriptionRenewals => Set<SubscriptionRenewal>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<WebhookSecret> WebhookSecrets => Set<WebhookSecret>();
    public DbSet<StorefrontLedgerAccounts> StorefrontLedgerAccounts => Set<StorefrontLedgerAccounts>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureJobRuns();

        modelBuilder.Entity<Subscription>(subscription =>
        {
            subscription.Property(x => x.BillingPeriod).HasConversion<string>().HasMaxLength(12);
            subscription.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            subscription.Property(x => x.Currency).HasMaxLength(3);
            subscription.Property(x => x.CustomerEmail).HasMaxLength(256);
            subscription.Property(x => x.ProviderCustomerId).HasMaxLength(255);
            subscription.Property(x => x.ProviderPaymentMethodId).HasMaxLength(255);
            subscription.HasIndex(x => new { x.OrderId, x.ProductId, x.VariantId }).IsUnique();
            subscription.HasIndex(x => new { x.TenantId, x.CustomerEmail });
            // Renewal history hangs off the aggregate's backing field, saved in the same unit of work.
            subscription.HasMany(x => x.Renewals).WithOne().HasForeignKey(r => r.SubscriptionId);
            subscription.Metadata.FindNavigation(nameof(Subscription.Renewals))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SubscriptionRenewal>(renewal =>
        {
            renewal.Property(x => x.Currency).HasMaxLength(3);
            renewal.HasIndex(x => x.SubscriptionId);
            renewal.HasIndex(x => new { x.SubscriptionId, x.Sequence }).IsUnique();
        });

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
            l.Property(x => x.Currency).HasMaxLength(3);
            l.HasIndex(x => new { x.AccountCode, x.Currency }); // per-(account,currency) balance rollups
            l.ToTable(t =>
            {
                t.HasCheckConstraint("ck_line_nonneg", "\"DebitMinor\" >= 0 AND \"CreditMinor\" >= 0");
                t.HasCheckConstraint("ck_line_one_side", "(\"DebitMinor\" = 0) <> (\"CreditMinor\" = 0)");
            });
        });

        modelBuilder.Entity<StorefrontLedgerAccounts>(s =>
        {
            s.HasKey(x => x.StorefrontId);
            s.Property(x => x.ReceivableAccountCode).HasMaxLength(80);
            s.Property(x => x.RevenueAccountCode).HasMaxLength(80);
            s.Property(x => x.TaxAccountCode).HasMaxLength(80);
        });

        modelBuilder.Entity<Payment>(p =>
        {
            p.HasIndex(x => x.OrderId).IsUnique();
            p.HasIndex(x => x.PaymentIntentId);
            p.Property(x => x.Currency).HasMaxLength(3);

            // Numeric enum on the wire and at rest (platform invariant); legacy rows backfill to
            // Card / stripe, which is exactly what they were before the column existed.
            p.Property(x => x.MethodKind).HasDefaultValue(PaymentMethodKind.Card);
            p.Property(x => x.Provider).HasMaxLength(40).HasDefaultValue(LedgerProviders.Default);

            // Legacy rows predate the dispute sub-status column; None is exactly their prior meaning.
            p.Property(x => x.DisputeStatus).HasDefaultValue(DisputeStatus.None);
            p.Property(x => x.ProviderDisputeId).HasMaxLength(255);
        });

        modelBuilder.Entity<VoidPayment>(v =>
        {
            // One void record per original payment — the processor upserts idempotently on this.
            v.HasIndex(x => x.OriginalPaymentId).IsUnique();
            v.HasIndex(x => x.OrderId);
            v.Property(x => x.Currency).HasMaxLength(3);
            v.Property(x => x.PaymentIntentId).HasMaxLength(255);
            v.Property(x => x.ProviderDisputeId).HasMaxLength(255);
            v.Property(x => x.Reason).HasMaxLength(120);
        });

        modelBuilder.Entity<Mandate>(m =>
        {
            m.HasIndex(x => x.UserId);
            m.HasIndex(x => x.ProviderSetupIntentId);
            m.Property(x => x.Provider).HasMaxLength(40);
            m.Property(x => x.Currency).HasMaxLength(3);
            m.Property(x => x.ProviderSetupIntentId).HasMaxLength(255);
            m.Property(x => x.ProviderMandateId).HasMaxLength(255);
            m.Property(x => x.ProviderPaymentMethodId).HasMaxLength(255);
        });

        modelBuilder.Entity<Refund>(r => r.HasIndex(x => x.OrderId));

        modelBuilder.Entity<PaymentAccount>(account =>
        {
            account.Property(x => x.Name).HasMaxLength(120);
            account.Property(x => x.Provider).HasMaxLength(40);
            account.Property(x => x.ExternalAccountRef).HasMaxLength(200);
            account.Property(x => x.WebhookEndpointId).HasMaxLength(255);
            account.Property(x => x.WebhookUrl).HasMaxLength(500);
            account.HasIndex(x => new { x.TenantId, x.StorefrontId, x.IsDefaultForStorefront });
        });

        modelBuilder.Entity<SupplierBankAccount>(account =>
        {
            account.Property(x => x.AccountName).HasMaxLength(160);
            account.Property(x => x.BankCountry).HasMaxLength(2);
            account.Property(x => x.RoutingNumberMasked).HasMaxLength(40);
            account.Property(x => x.AccountNumberMasked).HasMaxLength(40);
            account.Property(x => x.AccountTokenRef).HasMaxLength(200);
            account.Property(x => x.ApprovalReason).HasMaxLength(500);
            account.HasIndex(x => new { x.TenantId, x.SupplierEntityId, x.State });
        });

        modelBuilder.Entity<PayoutInstruction>(instruction =>
        {
            instruction.HasIndex(x => new { x.TenantId, x.SupplierEntityId, x.Active });
        });

        modelBuilder.Entity<SupplierPayablePolicy>(policy =>
        {
            policy.HasIndex(x => new { x.TenantId, x.SupplierEntityId, x.Active });
        });

        modelBuilder.Entity<SupplierPayable>(payable =>
        {
            payable.Property(x => x.Currency).HasMaxLength(3);
            payable.HasIndex(x => new { x.TenantId, x.SupplierEntityId, x.OrderId });
        });

        modelBuilder.Entity<PaymentCustomer>(customer =>
        {
            customer.Property(x => x.Provider).HasMaxLength(40);
            customer.Property(x => x.ProviderCustomerId).HasMaxLength(200);
            customer.HasIndex(x => new { x.TenantId, x.UserId, x.Provider }).IsUnique();
            customer.HasMany(x => x.PaymentMethods).WithOne().HasForeignKey(x => x.PaymentCustomerId);
        });

        modelBuilder.Entity<SavedPaymentMethod>(method =>
        {
            method.Property(x => x.Provider).HasMaxLength(40);
            method.Property(x => x.ProviderPaymentMethodId).HasMaxLength(200);
            method.Property(x => x.Brand).HasMaxLength(40);
            method.Property(x => x.Last4).HasMaxLength(4);
            method.HasIndex(x => new { x.TenantId, x.UserId, x.State });
            method.HasIndex(x => new { x.PaymentCustomerId, x.ProviderPaymentMethodId }).IsUnique();
        });

        modelBuilder.Entity<WebhookInboxEntry>().HasKey(x => x.EventId);

        // Signing-secret registry (def_2): platform-scoped (webhooks carry no tenant); resolution
        // filters Provider+Active, newest first.
        modelBuilder.Entity<WebhookSecret>(ws =>
        {
            ws.Property(s => s.Provider).HasMaxLength(32);
            ws.Property(s => s.Label).HasMaxLength(128);
            ws.HasIndex(s => new { s.Provider, s.Active });
        });
        modelBuilder.Entity<IdempotencyRecord>().HasKey(x => x.Key);
        modelBuilder.Entity<SyncRun>(s =>
        {
            s.HasKey(x => x.Id);
            s.HasIndex(x => x.Reference).IsUnique();
        });

        modelBuilder.Entity<XeroAccountMapping>(mapping =>
        {
            mapping.Property(x => x.LedgerAccountCode).HasMaxLength(80);
            mapping.Property(x => x.XeroAccountCode).HasMaxLength(40);
            mapping.HasIndex(x => new { x.TenantId, x.Scope, x.LedgerAccountCode });
        });

        // MassTransit transactional outbox + inbox tables (ADR-0007).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
