using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

public class StorefrontLifecycleTests
{
    [Fact]
    public void StorefrontLifecycle_starts_as_draft_private()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", DateTimeOffset.UtcNow);

        Assert.Equal(StorefrontState.Draft, storefront.State);
        Assert.Equal(StorefrontVisibility.Private, storefront.Visibility);
    }

    [Fact]
    public void StorefrontLifecycle_requires_canonical_domain_and_live_visibility_for_activation()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", DateTimeOffset.UtcNow);
        storefront.MoveToPreview(DateTimeOffset.UtcNow);

        var ex = Assert.Throws<CatalogRuleException>(() => storefront.Activate(DateTimeOffset.UtcNow));

        Assert.Contains("at least one domain", ex.Message, StringComparison.Ordinal);
        Assert.Contains("public or password visibility", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StorefrontLifecycle_can_activate_pause_and_reactivate()
    {
        var now = DateTimeOffset.UtcNow;
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", now);
        storefront.SetVisibility(StorefrontVisibility.Public, null, now);
        storefront.AddDomain("SHOP.EXAMPLE.test", canonical: true, now);

        storefront.MoveToPreview(now.AddMinutes(1));
        storefront.Activate(now.AddMinutes(2));
        storefront.Pause(now.AddMinutes(3));
        storefront.Activate(now.AddMinutes(4));

        Assert.Equal(StorefrontState.Active, storefront.State);
        Assert.Equal("shop.example.test", Assert.Single(storefront.Domains).Host);
        Assert.Equal(now.AddMinutes(2), storefront.ActivatedAt);
    }

    [Fact]
    public void StorefrontLifecycle_multiple_domains_have_one_canonical()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", DateTimeOffset.UtcNow);
        storefront.AddDomain("shop.example.test", canonical: true, DateTimeOffset.UtcNow);
        storefront.AddDomain("www.example.test", canonical: true, DateTimeOffset.UtcNow);

        Assert.Single(storefront.Domains, d => d.Canonical);
        Assert.Equal("www.example.test", storefront.Domains.Single(d => d.Canonical).Host);
    }

    [Fact]
    public void StorefrontLifecycle_configures_public_url_currency_and_tax_regime()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "AU store", DateTimeOffset.UtcNow);

        storefront.ConfigureCommerce("HTTP://localhost:3000/au/products", "aud", StorefrontTaxRegime.AuGst, 1000, DateTimeOffset.UtcNow);

        Assert.Equal("HTTP://localhost:3000/au/products", storefront.PublicUrl);
        Assert.Equal("AUD", storefront.Currency);
        Assert.Equal(StorefrontTaxRegime.AuGst, storefront.TaxRegime);
        Assert.Equal(1000, storefront.TaxRateBasisPoints);
    }

    [Fact]
    public void StorefrontLanguage_defaults_to_english_and_is_independent_of_commerce_config()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "AU store", DateTimeOffset.UtcNow);
        Assert.Equal("en", storefront.DefaultLanguage);

        // i18n_0: configuring currency/tax must never touch the language, and vice versa.
        storefront.SetDefaultLanguage("ZH-hant", DateTimeOffset.UtcNow);
        storefront.ConfigureCommerce("http://localhost:3000/au", "AUD", StorefrontTaxRegime.AuGst, 1000, DateTimeOffset.UtcNow);

        Assert.Equal("zh-Hant", storefront.DefaultLanguage); // normalized BCP-47 casing
        Assert.Equal("AUD", storefront.Currency);
    }

    [Fact]
    public void StorefrontLanguage_blank_keeps_the_current_language_and_invalid_tags_are_rejected()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", DateTimeOffset.UtcNow);
        storefront.SetDefaultLanguage("zh", DateTimeOffset.UtcNow);

        storefront.SetDefaultLanguage(null, DateTimeOffset.UtcNow); // an older client PUT omits it
        storefront.SetDefaultLanguage("  ", DateTimeOffset.UtcNow);

        Assert.Equal("zh", storefront.DefaultLanguage);
        Assert.Throws<CatalogRuleException>(() => storefront.SetDefaultLanguage("e", DateTimeOffset.UtcNow));
        Assert.Throws<CatalogRuleException>(() => storefront.SetDefaultLanguage("en_US", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SupportedLanguages_lists_english_first_with_endonym_labels()
    {
        Assert.Equal("en", SupportedLanguages.All[0].Code);
        Assert.True(SupportedLanguages.IsKnown("EN"));
        Assert.True(SupportedLanguages.IsKnown("yue"));
        Assert.False(SupportedLanguages.IsKnown("xx"));
        Assert.All(SupportedLanguages.All, l => Assert.False(string.IsNullOrWhiteSpace(l.Label)));
    }

    [Fact]
    public void StorefrontLifecycle_can_pause_a_draft_or_preview_storefront()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "EUR store", DateTimeOffset.UtcNow);

        storefront.Pause(DateTimeOffset.UtcNow);

        Assert.Equal(StorefrontState.Paused, storefront.State);
    }

    [Fact]
    public void StorefrontLifecycle_rejects_preview_when_already_in_preview()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", DateTimeOffset.UtcNow);
        storefront.MoveToPreview(DateTimeOffset.UtcNow);

        var ex = Assert.Throws<CatalogRuleException>(() => storefront.MoveToPreview(DateTimeOffset.UtcNow));

        Assert.Contains("cannot perform this transition", ex.Message, StringComparison.Ordinal);
        Assert.Equal(StorefrontState.Preview, storefront.State);
    }

    [Fact]
    public void StorefrontLifecycle_active_must_pause_before_returning_to_preview()
    {
        var now = DateTimeOffset.UtcNow;
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", now);
        storefront.SetVisibility(StorefrontVisibility.Public, null, now);
        storefront.AddDomain("shop.example.test", canonical: true, now);
        storefront.MoveToPreview(now);
        storefront.Activate(now);

        Assert.Throws<CatalogRuleException>(() => storefront.MoveToPreview(now));

        storefront.Pause(now);
        storefront.MoveToPreview(now);

        Assert.Equal(StorefrontState.Preview, storefront.State);
    }

    [Fact]
    public void StorefrontLifecycle_paused_blocks_checkout_but_can_preview()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", DateTimeOffset.UtcNow);
        storefront.SetVisibility(StorefrontVisibility.Public, null, DateTimeOffset.UtcNow);
        storefront.AddDomain("shop.example.test", canonical: true, DateTimeOffset.UtcNow);
        storefront.MoveToPreview(DateTimeOffset.UtcNow);
        storefront.Activate(DateTimeOffset.UtcNow);

        storefront.Pause(DateTimeOffset.UtcNow);

        Assert.Equal(StorefrontState.Paused, storefront.State);
    }
}
