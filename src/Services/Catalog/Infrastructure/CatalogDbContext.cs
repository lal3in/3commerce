using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Infrastructure;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Variant> Variants => Set<Variant>();
    public DbSet<VariantPrice> VariantPrices => Set<VariantPrice>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<PingRecord> Pings => Set<PingRecord>();
    public DbSet<Storefront> Storefronts => Set<Storefront>();
    public DbSet<StorefrontDomain> StorefrontDomains => Set<StorefrontDomain>();
    public DbSet<ProductIdentifier> ProductIdentifiers => Set<ProductIdentifier>();
    public DbSet<ProductBundleComponent> ProductBundleComponents => Set<ProductBundleComponent>();
    public DbSet<StorefrontNavigationItem> StorefrontNavigationItems => Set<StorefrontNavigationItem>();
    public DbSet<ProductPublication> ProductPublications => Set<ProductPublication>();
    public DbSet<ProductPublicationVariant> ProductPublicationVariants => Set<ProductPublicationVariant>();
    public DbSet<Offer> Offers => Set<Offer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("catalog");

        modelBuilder.Entity<Offer>(offer =>
        {
            offer.Property(o => o.SupplyCategory).HasConversion<string>().HasMaxLength(16);
            offer.Property(o => o.FulfilmentType).HasConversion<string>().HasMaxLength(24);
            offer.Property(o => o.PricingModel).HasConversion<string>().HasMaxLength(24);
            offer.Property(o => o.BillingPeriod).HasConversion<string>().HasMaxLength(12);
            offer.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
            offer.Property(o => o.Currency).HasMaxLength(3);
            offer.HasMany(o => o.PriceTiers).WithOne().HasForeignKey(t => t.OfferId);
            offer.HasIndex(o => new { o.TenantId, o.ProductId, o.VariantId });
            offer.HasIndex(o => new { o.TenantId, o.SupplierId });
        });

        modelBuilder.Entity<Product>(product =>
        {
            product.HasIndex(p => new { p.TenantId, p.Slug }).IsUnique();
            product.Property(p => p.Attributes).HasColumnType("jsonb");
            product.Property(p => p.ImageUrls).HasColumnType("jsonb");
            product.HasMany(p => p.Variants).WithOne().HasForeignKey(v => v.ProductId);
            product.HasMany(p => p.Identifiers).WithOne().HasForeignKey(i => i.ProductId);
            product.HasMany(p => p.BundleComponents).WithOne().HasForeignKey(c => c.BundleProductId);
            product.HasIndex(p => new { p.TenantId, p.CategoryId });
            // search_vector (weighted tsvector) + GIN indexes are raw SQL in the
            // SearchSchema migration — deliberately not part of the EF model.
        });

        modelBuilder.Entity<Variant>(variant =>
        {
            variant.HasIndex(v => v.Sku).IsUnique();
            variant.Property(v => v.Currency).HasMaxLength(3);
            variant.Property(v => v.Barcode).HasMaxLength(80);
            variant.HasMany(v => v.Prices).WithOne().HasForeignKey(p => p.VariantId);
        });

        modelBuilder.Entity<VariantPrice>(price =>
        {
            price.Property(p => p.Currency).HasMaxLength(3);
            price.HasIndex(p => new { p.VariantId, p.Currency }).IsUnique();
        });

        modelBuilder.Entity<Category>(category =>
        {
            category.HasIndex(c => new { c.TenantId, c.Slug }).IsUnique();
        });

        modelBuilder.Entity<ImportRun>(run =>
        {
            run.Property(r => r.SampleRejections).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Storefront>(storefront =>
        {
            storefront.ToTable("Storefronts");
            storefront.HasKey(s => s.Id);
            storefront.Property(s => s.Name).HasMaxLength(120);
            storefront.Property(s => s.AccessPasswordHash).HasMaxLength(500);
            storefront.Property(s => s.PublicUrl).HasMaxLength(300);
            storefront.Property(s => s.Currency).HasMaxLength(3);
            // BCP-47 default UI language (i18n_0) — independent of Currency/TaxRegime.
            storefront.Property(s => s.DefaultLanguage).HasMaxLength(16).HasDefaultValue(SupportedLanguages.Default);
            storefront.Property(s => s.TaxRegime).HasConversion<string>().HasMaxLength(24);
            storefront.HasIndex(s => new { s.TenantId, s.Name }).IsUnique();
            storefront.HasIndex(s => new { s.TenantId, s.State });
            storefront.HasMany(s => s.Domains).WithOne().HasForeignKey(d => d.StorefrontId);
        });

        modelBuilder.Entity<StorefrontDomain>(domain =>
        {
            domain.ToTable("StorefrontDomains");
            domain.HasKey(d => d.Id);
            domain.Property(d => d.Host).HasMaxLength(253);
            domain.HasIndex(d => d.Host).IsUnique();
            domain.HasIndex(d => new { d.StorefrontId, d.Canonical });
        });

        modelBuilder.Entity<ProductIdentifier>(identifier =>
        {
            identifier.ToTable("ProductIdentifiers");
            identifier.HasKey(i => i.Id);
            identifier.Property(i => i.Value).HasMaxLength(80);
            identifier.HasIndex(i => new { i.Type, i.Value });
        });

        modelBuilder.Entity<ProductBundleComponent>(component =>
        {
            component.ToTable("ProductBundleComponents");
            component.HasKey(c => c.Id);
            component.HasIndex(c => new { c.BundleProductId, c.ComponentProductId, c.ComponentVariantId }).IsUnique();
        });

        modelBuilder.Entity<StorefrontNavigationItem>(item =>
        {
            item.ToTable("StorefrontNavigationItems");
            item.HasKey(i => i.Id);
            item.Property(i => i.Label).HasMaxLength(120);
            item.HasIndex(i => new { i.TenantId, i.StorefrontId, i.SortOrder });
        });

        modelBuilder.Entity<ProductPublication>(publication =>
        {
            publication.ToTable("ProductPublications");
            publication.HasKey(p => p.Id);
            publication.Property(p => p.SlugOverride).HasMaxLength(120);
            publication.Property(p => p.TitleOverride).HasMaxLength(200);
            publication.Property(p => p.DescriptionOverride).HasMaxLength(4000);
            publication.Property(p => p.SeoTitle).HasMaxLength(70);
            publication.Property(p => p.SeoDescription).HasMaxLength(180);
            publication.Property(p => p.CountryOfOrigin).HasMaxLength(2);
            publication.Property(p => p.HarmonizedSystemCode).HasMaxLength(20);
            publication.HasMany(p => p.Variants).WithOne().HasForeignKey(v => v.PublicationId);
            publication.HasIndex(p => new { p.TenantId, p.StorefrontId, p.ProductId }).IsUnique();
            publication.HasIndex(p => new { p.TenantId, p.StorefrontId, p.State });
        });

        modelBuilder.Entity<ProductPublicationVariant>(variant =>
        {
            variant.ToTable("ProductPublicationVariants");
            variant.HasKey(v => v.Id);
            variant.Property(v => v.SkuOverride).HasMaxLength(80);
            variant.HasIndex(v => new { v.PublicationId, v.VariantId }).IsUnique();
        });

        // MassTransit transactional outbox + inbox tables (ADR-0007).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
