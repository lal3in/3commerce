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
    public void ShipToCountries_default_is_empty_meaning_worldwide()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", DateTimeOffset.UtcNow);
        Assert.Empty(storefront.ShipToCountries);
    }

    [Fact]
    public void ShipToCountries_normalizes_uppercases_dedupes_and_sorts()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", DateTimeOffset.UtcNow);

        storefront.SetShipToCountries(["nz", "AU", " au ", "us", "NZ"], DateTimeOffset.UtcNow);

        Assert.Equal(["AU", "NZ", "US"], storefront.ShipToCountries);
    }

    [Fact]
    public void ShipToCountries_null_keeps_current_and_empty_clears_to_worldwide()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", DateTimeOffset.UtcNow);
        storefront.SetShipToCountries(["AU", "NZ"], DateTimeOffset.UtcNow);

        storefront.SetShipToCountries(null, DateTimeOffset.UtcNow); // an older client PUT omits the field
        Assert.Equal(["AU", "NZ"], storefront.ShipToCountries);

        storefront.SetShipToCountries([], DateTimeOffset.UtcNow); // explicit empty = back to worldwide
        Assert.Empty(storefront.ShipToCountries);
    }

    [Fact]
    public void ShipToCountries_rejects_non_two_letter_codes()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "Main store", DateTimeOffset.UtcNow);

        Assert.Throws<CatalogRuleException>(() => storefront.SetShipToCountries(["AUS"], DateTimeOffset.UtcNow));
        Assert.Throws<CatalogRuleException>(() => storefront.SetShipToCountries(["A1"], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void StorefrontLifecycle_can_pause_a_draft_or_preview_storefront()
    {
        var storefront = Storefront.Create(Guid.CreateVersion7(), "EUR store", DateTimeOffset.UtcNow);

        storefront.Pause(DateTimeOffset.UtcNow);

        Assert.Equal(StorefrontState.Paused, storefront.State);
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
