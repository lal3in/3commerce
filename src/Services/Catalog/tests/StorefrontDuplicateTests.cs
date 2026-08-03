using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

public class StorefrontDuplicateTests
{
    private static Storefront ConfiguredSource()
    {
        var now = DateTimeOffset.UtcNow;
        var source = Storefront.Create(Guid.CreateVersion7(), "EU store", now);
        source.ConfigureCommerce("http://localhost:3000/eu", "EUR", StorefrontTaxRegime.EuVat, 2000, now);
        source.SetDefaultLanguage("de", now);
        source.SetTheme("""{"colorPrimary":"#0a0a0a","radius":"1rem"}""", now);
        source.SetLedgerAccounts("revenue.custom", null, null, now, null);
        source.SetVisibility(StorefrontVisibility.Public, null, now);
        source.AddDomain("shop.eu.test", canonical: true, now);
        return source;
    }

    [Fact]
    public void DuplicateFrom_derives_fresh_ledger_codes_containing_the_new_id()
    {
        var source = ConfiguredSource();

        var clone = Storefront.DuplicateFrom(source, "EU store copy", DateTimeOffset.UtcNow);

        Assert.NotEqual(source.Id, clone.Id);
        // Fresh, auto-derived codes keyed on the NEW id — never the source's (even the operator-set one).
        Assert.Equal($"receivable.store-{clone.Id:N}", clone.ReceivableAccountCode);
        Assert.Equal($"revenue.store-{clone.Id:N}", clone.RevenueAccountCode);
        Assert.Equal($"tax.store-{clone.Id:N}", clone.TaxAccountCode);
        Assert.Equal($"shipping.store-{clone.Id:N}", clone.ShippingAccountCode);
        Assert.DoesNotContain(source.Id.ToString("N"), clone.RevenueAccountCode, StringComparison.Ordinal);
        Assert.NotEqual(source.RevenueAccountCode, clone.RevenueAccountCode);
    }

    [Fact]
    public void DuplicateFrom_copies_currency_tax_language_and_theme()
    {
        var source = ConfiguredSource();

        var clone = Storefront.DuplicateFrom(source, "EU store copy", DateTimeOffset.UtcNow);

        Assert.Equal(source.Currency, clone.Currency);
        Assert.Equal(source.TaxRegime, clone.TaxRegime);
        Assert.Equal(source.TaxRateBasisPoints, clone.TaxRateBasisPoints);
        Assert.Equal(source.DefaultLanguage, clone.DefaultLanguage);
        Assert.Equal(source.ThemeJson, clone.ThemeJson);
    }

    [Fact]
    public void DuplicateFrom_starts_as_a_private_draft_with_no_domains_or_public_url()
    {
        var source = ConfiguredSource();

        var clone = Storefront.DuplicateFrom(source, "EU store copy", DateTimeOffset.UtcNow);

        Assert.Equal(StorefrontState.Draft, clone.State);
        Assert.Equal(StorefrontVisibility.Private, clone.Visibility);
        Assert.Empty(clone.Domains);
        Assert.Equal("", clone.PublicUrl);
        Assert.Null(clone.AccessPasswordHash);
    }

    [Fact]
    public void DuplicateFrom_clones_a_never_configured_storefront_cleanly()
    {
        var now = DateTimeOffset.UtcNow;
        var bare = Storefront.Create(Guid.CreateVersion7(), "Bare store", now);

        var clone = Storefront.DuplicateFrom(bare, "Bare store copy", now);

        Assert.Equal("EUR", clone.Currency); // the aggregate default
        Assert.Equal(StorefrontTaxRegime.None, clone.TaxRegime);
        Assert.Equal("", clone.ThemeJson);
        Assert.Equal($"revenue.store-{clone.Id:N}", clone.RevenueAccountCode);
    }

    // The publication + navigation copy lives in the duplicate endpoint (it is DB-bound), but the fidelity
    // rule it relies on — carrying the source publication's State and per-variant visibility — is a domain
    // method (CopyPublicationStateFrom) and is asserted here directly.
    [Fact]
    public void CopyPublicationStateFrom_keeps_a_published_publication_published_with_hidden_variants_hidden()
    {
        var now = DateTimeOffset.UtcNow;
        var product = PublishableProduct();
        var hiddenVariantId = product.Variants[1].Id;

        // Source: publish it, but hide the second variant.
        var source = ProductPublication.Assign(product.TenantId, Guid.CreateVersion7(), product, now);
        source.SetFulfillment(FulfilmentType.Dropship, "au", "8518", now);
        source.Variants.Single(v => v.VariantId == hiddenVariantId).Visible = false;
        source.Publish(product, now);
        Assert.Equal(PublicationState.Published, source.State);

        // Clone target: a fresh assignment on a different storefront (all variants visible, Draft).
        var clonedStorefrontId = Guid.CreateVersion7();
        var target = ProductPublication.Assign(product.TenantId, clonedStorefrontId, product, now);
        target.SetFulfillment(source.FulfillmentSource, source.CountryOfOrigin, source.HarmonizedSystemCode, now);

        target.CopyPublicationStateFrom(source, now);

        Assert.Equal(PublicationState.Published, target.State);
        Assert.NotNull(target.PublishedAt);
        Assert.False(target.Variants.Single(v => v.VariantId == hiddenVariantId).Visible);
        Assert.True(target.Variants.Single(v => v.VariantId == product.Variants[0].Id).Visible);
    }

    private static Product PublishableProduct()
    {
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            Slug = "sample",
            Title = "Sample product",
            Brand = "Sample",
            CategoryId = Guid.CreateVersion7(),
            ImageUrls = ["https://example.test/sample.png"],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        // Shippable variants need item + package weight/dimensions to be publish-ready (physical readiness).
        static Variant Dims(Variant v) { v.WeightGrams = 500; v.LengthMm = 200; v.WidthMm = 150; v.HeightMm = 100; v.PackageWeightGrams = 650; v.PackageLengthMm = 250; v.PackageWidthMm = 200; v.PackageHeightMm = 150; return v; }
        product.Variants.Add(Dims(new Variant { Id = Guid.CreateVersion7(), ProductId = product.Id, Sku = "SKU-1", PriceMinor = 1000, Currency = "EUR", StockQuantity = 10 }));
        product.Variants.Add(Dims(new Variant { Id = Guid.CreateVersion7(), ProductId = product.Id, Sku = "SKU-2", PriceMinor = 2000, Currency = "EUR", StockQuantity = 5 }));
        return product;
    }
}
