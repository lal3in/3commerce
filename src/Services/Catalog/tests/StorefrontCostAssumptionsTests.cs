using System.Text.Json;
using ThreeCommerce.Catalog.Domain;

namespace ThreeCommerce.Catalog.Tests;

/// <summary>
/// Per-storefront cost assumptions (phase 4): a validated JSON object of the five known basis-point
/// keys, each 0–10000. Config only — never posts to the ledger. Mirrors the theme sanitizer's null-leaves,
/// unknown-key, range and duplicate-key guards.
/// </summary>
public class StorefrontCostAssumptionsTests
{
    private static Storefront NewStorefront() =>
        Storefront.Create(Guid.CreateVersion7(), "Cost store", DateTimeOffset.UtcNow);

    [Fact]
    public void SetCostAssumptions_accepts_the_five_known_bps_keys()
    {
        var storefront = NewStorefront();
        const string json = """
            {"packagingBps":150,"laborBps":300,"marketingBps":500,"insuranceBps":75,"bufferBps":2000}
            """;

        storefront.SetCostAssumptions(json, DateTimeOffset.UtcNow);

        var rates = JsonSerializer.Deserialize<Dictionary<string, int>>(storefront.CostAssumptionsJson)!;
        Assert.Equal(150, rates["packagingBps"]);
        Assert.Equal(300, rates["laborBps"]);
        Assert.Equal(500, rates["marketingBps"]);
        Assert.Equal(75, rates["insuranceBps"]);
        Assert.Equal(2000, rates["bufferBps"]);
    }

    [Fact]
    public void SetCostAssumptions_null_or_blank_leaves_the_current_value()
    {
        var storefront = NewStorefront();
        storefront.SetCostAssumptions("""{"packagingBps":150}""", DateTimeOffset.UtcNow);
        var before = storefront.CostAssumptionsJson;

        storefront.SetCostAssumptions(null, DateTimeOffset.UtcNow);
        storefront.SetCostAssumptions("   ", DateTimeOffset.UtcNow);

        Assert.Equal(before, storefront.CostAssumptionsJson);
    }

    [Fact]
    public void SetCostAssumptions_rejects_an_unknown_key()
    {
        var storefront = NewStorefront();
        Assert.Throws<CatalogRuleException>(() =>
            storefront.SetCostAssumptions("""{"packagingBps":100,"shippingBps":200}""", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public void SetCostAssumptions_rejects_out_of_range_bps(int bps)
    {
        var storefront = NewStorefront();
        Assert.Throws<CatalogRuleException>(() =>
            storefront.SetCostAssumptions($$"""{"packagingBps":{{bps}}}""", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SetCostAssumptions_rejects_a_non_integer_value()
    {
        var storefront = NewStorefront();
        Assert.Throws<CatalogRuleException>(() =>
            storefront.SetCostAssumptions("""{"packagingBps":"lots"}""", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SetCostAssumptions_rejects_a_non_object()
    {
        var storefront = NewStorefront();
        Assert.Throws<CatalogRuleException>(() =>
            storefront.SetCostAssumptions("[1,2,3]", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SetCostAssumptions_rejects_duplicate_keys()
    {
        var storefront = NewStorefront();
        Assert.Throws<CatalogRuleException>(() =>
            storefront.SetCostAssumptions("""{"packagingBps":100,"packagingBps":200}""", DateTimeOffset.UtcNow));
        Assert.Equal(string.Empty, storefront.CostAssumptionsJson);
    }

    [Fact]
    public void DuplicateFrom_carries_the_source_cost_assumptions()
    {
        var source = NewStorefront();
        source.SetCostAssumptions("""{"packagingBps":150,"bufferBps":2000}""", DateTimeOffset.UtcNow);

        var clone = Storefront.DuplicateFrom(source, "Cost store (copy)", DateTimeOffset.UtcNow);

        Assert.Equal(source.CostAssumptionsJson, clone.CostAssumptionsJson);
    }
}
