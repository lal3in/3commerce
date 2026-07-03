using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Tests;

/// <summary>
/// ProductVariantCopy.PriceInCurrency — the pure per-currency resolution the cart, checkout
/// revalidation, and merge-on-login all price against (review remediation rev_4 / finding F3).
/// </summary>
public class ProductCopyPriceTests
{
    private static ProductVariantCopy Variant(long baseMinor = 1_000, string baseCurrency = "EUR", params (string Currency, long PriceMinor)[] prices)
    {
        var variant = new ProductVariantCopy
        {
            VariantId = Guid.CreateVersion7(),
            ProductId = Guid.CreateVersion7(),
            Sku = "SKU-1",
            PriceMinor = baseMinor,
            Currency = baseCurrency,
        };
        foreach (var (currency, priceMinor) in prices)
        {
            variant.Prices.Add(new ProductVariantCopyPrice
            {
                Id = Guid.CreateVersion7(),
                VariantId = variant.VariantId,
                Currency = currency,
                PriceMinor = priceMinor,
            });
        }

        return variant;
    }

    [Fact]
    public void Per_currency_override_wins()
    {
        var variant = Variant(1_000, "EUR", ("AUD", 1_650));
        Assert.Equal(1_650, variant.PriceInCurrency("AUD"));
    }

    [Fact]
    public void Override_beats_the_base_even_for_the_base_currency()
    {
        var variant = Variant(1_000, "EUR", ("EUR", 900));
        Assert.Equal(900, variant.PriceInCurrency("EUR"));
    }

    [Fact]
    public void Base_price_serves_its_own_currency_when_no_override_exists()
    {
        var variant = Variant(1_000, "EUR");
        Assert.Equal(1_000, variant.PriceInCurrency("EUR"));
    }

    [Fact]
    public void Unpriced_currency_returns_null_so_the_product_is_hidden_there()
    {
        var variant = Variant(1_000, "EUR", ("AUD", 1_650));
        Assert.Null(variant.PriceInCurrency("JPY"));
    }

    [Fact]
    public void Currency_lookup_is_case_and_whitespace_insensitive()
    {
        var variant = Variant(1_000, "EUR", ("AUD", 1_650));
        Assert.Equal(1_650, variant.PriceInCurrency(" aud "));
        Assert.Equal(1_000, variant.PriceInCurrency("eur"));
    }
}
