using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure.Sagas;

namespace ThreeCommerce.Ordering.Infrastructure;

public class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<ProductCopy> ProductCopies => Set<ProductCopy>();
    public DbSet<ProductVariantCopy> ProductVariantCopies => Set<ProductVariantCopy>();
    public DbSet<ProductVariantCopyPrice> ProductVariantCopyPrices => Set<ProductVariantCopyPrice>();
    public DbSet<StorefrontTaxCopy> StorefrontTaxCopies => Set<StorefrontTaxCopy>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<CheckoutAttempt> CheckoutAttempts => Set<CheckoutAttempt>();
    public DbSet<CheckoutAttemptLine> CheckoutAttemptLines => Set<CheckoutAttemptLine>();
    public DbSet<OrderNumberSequence> OrderNumberSequences => Set<OrderNumberSequence>();
    public DbSet<CheckoutState> CheckoutStates => Set<CheckoutState>();
    public DbSet<OfferCopy> OfferCopies => Set<OfferCopy>();
    public DbSet<PromotionCopy> PromotionCopies => Set<PromotionCopy>();
    public DbSet<PromotionRedemption> PromotionRedemptions => Set<PromotionRedemption>();
    public DbSet<ProductTypeShippingPolicyCopy> ProductTypeShippingPolicyCopies => Set<ProductTypeShippingPolicyCopy>();
    public DbSet<VerifiedCustomerCopy> VerifiedCustomerCopies => Set<VerifiedCustomerCopy>();
    public DbSet<SupplierApprovalCopy> SupplierApprovalCopies => Set<SupplierApprovalCopy>();
    public DbSet<SupplierWarehouseCopy> SupplierWarehouseCopies => Set<SupplierWarehouseCopy>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("ordering");

        modelBuilder.Entity<OfferCopy>(offer =>
        {
            offer.HasKey(o => o.OfferId);
            offer.Property(o => o.FulfilmentType).HasConversion<string>().HasMaxLength(24);
            offer.Property(o => o.Currency).HasMaxLength(3);
            // ProductType stays int-backed (not string): a new column defaults existing rows to 0
            // ("unknown"), which checkout treats as a fulfilment-type decision until the offer is
            // re-projected — never mis-typing a legacy copy as Physical.
            offer.HasIndex(o => new { o.TenantId, o.ProductId, o.VariantId });
        });

        // Threshold promotions (ADR-0051). The covering index matters: checkout and every /cart/summary
        // call load the tenant's active promotions for the line's storefront (or all-storefront).
        modelBuilder.Entity<PromotionCopy>(promotion =>
        {
            promotion.HasKey(p => p.PromotionId);
            promotion.Property(p => p.Currency).HasMaxLength(3);
            promotion.Property(p => p.Name).HasMaxLength(120);
            promotion.Property(p => p.Scope).HasConversion<string>().HasMaxLength(16);
            promotion.Property(p => p.Code).HasMaxLength(40);
            promotion.HasIndex(p => new { p.TenantId, p.StorefrontId, p.Active });
            // Coupon lookup (ADR-0052): checkout and /cart/summary resolve the entered code to at most one
            // promotion per tenant. Not unique here — a read model must accept whatever Catalog projects
            // (Catalog owns the uniqueness rule), or a mis-projection would poison the consumer.
            promotion.HasIndex(p => new { p.TenantId, p.Code });
        });

        // Coupon redemptions (ADR-0052): Ordering-owned, reserved at checkout and confirmed/released by
        // the saga's terminal transitions.
        modelBuilder.Entity<PromotionRedemption>(redemption =>
        {
            redemption.HasKey(r => r.Id);
            redemption.Property(r => r.CustomerKey).HasMaxLength(340);
            redemption.Property(r => r.Code).HasMaxLength(40);
            redemption.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            // Idempotency: one redemption per (promotion, order), whatever a retried checkout or a
            // redelivered message tries. This unique index — not application logic — is the guarantee.
            redemption.HasIndex(r => new { r.PromotionId, r.OrderId }).IsUnique();
            // The per-customer limit's count query.
            redemption.HasIndex(r => new { r.PromotionId, r.CustomerKey });
            // Confirm/release address a redemption by the order alone (the saga knows nothing else).
            redemption.HasIndex(r => r.OrderId);
        });

        modelBuilder.Entity<ProductTypeShippingPolicyCopy>(policy =>
        {
            policy.HasKey(p => p.TenantId);
            policy.Property(p => p.RequiresShippingTypes).HasMaxLength(200);
        });

        modelBuilder.Entity<ProductCopy>(product =>
        {
            product.HasKey(p => p.ProductId);
            product.HasIndex(p => p.Slug);
            product.HasMany(p => p.Variants).WithOne().HasForeignKey(v => v.ProductId);
            // Per-country ship rules as jsonb; List<record> needs an explicit ValueComparer (mirrors Catalog).
            product.Property(p => p.ShipRules)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<ProductShipRule>>(v, (JsonSerializerOptions?)null) ?? new(),
                    new ValueComparer<List<ProductShipRule>>(
                        (a, b) => a!.SequenceEqual(b!),
                        v => v.Aggregate(0, (h, r) => HashCode.Combine(h, r.GetHashCode())),
                        v => v.ToList()))
                .HasDefaultValueSql("'[]'::jsonb");
        });

        modelBuilder.Entity<ProductVariantCopy>(variant =>
        {
            variant.HasKey(v => v.VariantId);
            variant.HasIndex(v => new { v.ProductId, v.Sku });
            variant.Property(v => v.Currency).HasMaxLength(3);
            variant.HasMany(v => v.Prices).WithOne().HasForeignKey(p => p.VariantId);
        });

        modelBuilder.Entity<ProductVariantCopyPrice>(price =>
        {
            price.HasKey(p => p.Id);
            price.Property(p => p.Currency).HasMaxLength(3);
            price.HasIndex(p => new { p.VariantId, p.Currency }).IsUnique();
        });

        modelBuilder.Entity<StorefrontTaxCopy>(tax =>
        {
            tax.HasKey(t => t.StorefrontId);
            tax.Property(t => t.Currency).HasMaxLength(3);
            tax.Property(t => t.ShipToCountries)
                .HasConversion(
                    v => string.Join(',', v),
                    v => v.Length == 0 ? new List<string>() : v.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    new ValueComparer<List<string>>(
                        (a, b) => a!.SequenceEqual(b!),
                        v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode(StringComparison.Ordinal))),
                        v => v.ToList()))
                .HasMaxLength(1000);
            tax.HasIndex(t => new { t.Currency, t.IsLive });
        });

        modelBuilder.Entity<Cart>(c =>
        {
            c.HasIndex(x => x.CartKey);
            c.HasIndex(x => x.UserId);
            c.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.CartId);
            c.Navigation(x => x.Items).AutoInclude();
        });

        modelBuilder.Entity<Order>(o =>
        {
            o.Property(x => x.Currency).HasMaxLength(3);
            o.Property(x => x.PaymentOption).HasMaxLength(40);
            o.Property(x => x.PaymentInstrumentSummary).HasMaxLength(120);
            o.Property(x => x.PaymentProvider).HasMaxLength(40);
            // Comma-joined promotion ids (ADR-0051): a handful of GUIDs at most, bounded so the column
            // can't grow unbounded on a pathological cart.
            o.Property(x => x.AppliedPromotionIds).HasMaxLength(400);
            o.Property(x => x.CouponCode).HasMaxLength(40);
            o.HasIndex(x => new { x.StorefrontId, x.PublicOrderNumber }).IsUnique();
            o.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.OrderId);
        });

        modelBuilder.Entity<CheckoutAttempt>(attempt =>
        {
            attempt.Property(x => x.AppliedPromotionIds).HasMaxLength(400);
            attempt.Property(x => x.CouponCode).HasMaxLength(40);
            attempt.Property(x => x.Currency).HasMaxLength(3);
            attempt.Property(x => x.PaymentIntentId).HasMaxLength(200);
            attempt.Property(x => x.PaymentOption).HasMaxLength(40);
            attempt.Property(x => x.PaymentInstrumentSummary).HasMaxLength(120);
            attempt.Property(x => x.PaymentProvider).HasMaxLength(40);
            attempt.Property(x => x.CampaignRef).HasMaxLength(120);
            attempt.HasIndex(x => new { x.StorefrontId, x.Status });
            attempt.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.CheckoutAttemptId);
        });

        modelBuilder.Entity<OrderNumberSequence>(sequence =>
        {
            sequence.HasKey(x => x.StorefrontId);
        });

        modelBuilder.Entity<VerifiedCustomerCopy>(copy =>
        {
            copy.HasKey(x => x.Email);
            copy.Property(x => x.Email).HasMaxLength(320);
        });

        modelBuilder.Entity<SupplierApprovalCopy>(approval =>
        {
            approval.HasKey(x => x.SupplierId);
        });

        modelBuilder.Entity<SupplierWarehouseCopy>(warehouse =>
        {
            warehouse.HasKey(x => x.SupplierId);
            warehouse.Property(x => x.Name).HasMaxLength(200);
            warehouse.Property(x => x.Line1).HasMaxLength(200);
            warehouse.Property(x => x.Line2).HasMaxLength(200);
            warehouse.Property(x => x.City).HasMaxLength(200);
            warehouse.Property(x => x.Region).HasMaxLength(200);
            warehouse.Property(x => x.Postcode).HasMaxLength(200);
            warehouse.Property(x => x.CountryCode).HasMaxLength(2);
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
