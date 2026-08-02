using System.Text.Json;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

public class StorefrontThemeTests
{
    private static Storefront NewStorefront() =>
        Storefront.Create(Guid.CreateVersion7(), "Theme store", DateTimeOffset.UtcNow);

    [Fact]
    public void SetTheme_accepts_the_six_known_tokens()
    {
        var storefront = NewStorefront();
        const string json = """
            {"colorPrimary":"#ff0000","colorBg":"#ffffff","colorText":"#111827",
             "colorMuted":"#6b7280","fontSans":"system-ui, -apple-system, sans-serif","radius":"0.5rem"}
            """;

        storefront.SetTheme(json, DateTimeOffset.UtcNow);

        var tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(storefront.ThemeJson)!;
        Assert.Equal("#ff0000", tokens["colorPrimary"]);
        Assert.Equal("#ffffff", tokens["colorBg"]);
        Assert.Equal("#111827", tokens["colorText"]);
        Assert.Equal("#6b7280", tokens["colorMuted"]);
        Assert.Equal("system-ui, -apple-system, sans-serif", tokens["fontSans"]);
        Assert.Equal("0.5rem", tokens["radius"]);
    }

    [Fact]
    public void SetTheme_rejects_url_and_javascript_values()
    {
        var storefront = NewStorefront();

        Assert.Throws<CatalogRuleException>(() =>
            storefront.SetTheme("""{"colorBg":"url(javascript:alert(1))"}""", DateTimeOffset.UtcNow));
        Assert.Equal("", storefront.ThemeJson); // rejected → nothing stored
    }

    [Fact]
    public void SetTheme_rejects_rule_breaking_punctuation()
    {
        var storefront = NewStorefront();

        // A value that tries to close the CSS rule and inject another — the DANGEROUS [;{}<>] class.
        Assert.Throws<CatalogRuleException>(() =>
            storefront.SetTheme("""{"colorBg":"#fff};body{display:none"}""", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SetTheme_rejects_unknown_keys()
    {
        var storefront = NewStorefront();

        Assert.Throws<CatalogRuleException>(() =>
            storefront.SetTheme("""{"colorEvil":"#fff"}""", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SetTheme_rejects_values_over_100_chars()
    {
        var storefront = NewStorefront();
        var tooLong = new string('a', 101);

        Assert.Throws<CatalogRuleException>(() =>
            storefront.SetTheme($$"""{"fontSans":"{{tooLong}}"}""", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SetTheme_rejects_non_object_and_non_string_values()
    {
        var storefront = NewStorefront();

        Assert.Throws<CatalogRuleException>(() => storefront.SetTheme("\"just a string\"", DateTimeOffset.UtcNow));
        Assert.Throws<CatalogRuleException>(() => storefront.SetTheme("not json at all", DateTimeOffset.UtcNow));
        Assert.Throws<CatalogRuleException>(() => storefront.SetTheme("""{"radius":12}""", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SetTheme_rejects_duplicate_keys_in_raw_json()
    {
        var storefront = NewStorefront();

        // A hand-crafted body can carry duplicate keys the HTTP binder would never produce; JsonObject only
        // surfaces that as ArgumentException at enumeration, so this guards the public domain contract.
        Assert.Throws<CatalogRuleException>(() =>
            storefront.SetTheme("""{"radius":"1rem","radius":"2rem"}""", DateTimeOffset.UtcNow));
        Assert.Equal("", storefront.ThemeJson);
    }

    [Fact]
    public void SetTheme_blank_or_null_leaves_the_current_theme()
    {
        var storefront = NewStorefront();
        storefront.SetTheme("""{"colorPrimary":"#123456"}""", DateTimeOffset.UtcNow);
        var stored = storefront.ThemeJson;

        storefront.SetTheme(null, DateTimeOffset.UtcNow);
        storefront.SetTheme("   ", DateTimeOffset.UtcNow);
        storefront.SetTheme("", DateTimeOffset.UtcNow);

        Assert.Equal(stored, storefront.ThemeJson);
        Assert.Contains("#123456", storefront.ThemeJson, StringComparison.Ordinal);
    }
}
