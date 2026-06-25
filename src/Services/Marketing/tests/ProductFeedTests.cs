using ThreeCommerce.Marketing.Domain;

namespace ThreeCommerce.Marketing.Tests;

public class ProductFeedTests
{
    private static FeedProduct Product(
        string id = "p1", bool published = true, bool pub = true, long price = 1999, bool inStock = true) =>
        new(id, "Widget", "A widget", "widget", "https://cdn/x.png", price, "AUD", inStock, "Acme", published, pub);

    [Theory]
    [InlineData(true, true, 1999, true)]
    [InlineData(false, true, 1999, false)]  // draft
    [InlineData(true, false, 1999, false)]  // private
    [InlineData(true, true, 0, false)]      // unpriced
    public void Only_public_published_priced_products_are_eligible(bool published, bool pub, long price, bool eligible)
    {
        Assert.Equal(eligible, ProductFeed.IsEligible(Product(published: published, pub: pub, price: price)));
    }

    [Fact]
    public void Feed_item_carries_shopper_facing_offer_metadata()
    {
        var item = ProductFeed.ToItem(Product(), "https://shop.acme.com/");
        Assert.Equal("https://shop.acme.com/products/widget", item.Link);
        Assert.Equal("in_stock", item.Availability);
        Assert.Equal("19.99 AUD", item.Price);
        Assert.Equal("new", item.Condition);
    }

    [Fact]
    public void Out_of_stock_is_reflected()
    {
        Assert.Equal("out_of_stock", ProductFeed.ToItem(Product(inStock: false), "https://s").Availability);
    }

    [Fact]
    public void Generate_emits_a_header_and_only_eligible_rows()
    {
        var feed = ProductFeed.Generate(new FeedSettings(Enabled: true), new[]
        {
            Product("p1"),
            Product("p2", published: false), // excluded
            Product("p3", price: 0),         // excluded
        }, "https://shop.acme.com");

        Assert.NotNull(feed);
        var lines = feed!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length); // header + p1 only
        Assert.StartsWith("id,title,description,link,image_link,availability,price,brand,condition", lines[0]);
        Assert.Contains("p1", lines[1]);
    }

    [Fact]
    public void A_disabled_storefront_produces_no_feed()
    {
        Assert.Null(ProductFeed.Generate(new FeedSettings(Enabled: false), new[] { Product() }, "https://s"));
    }

    [Fact]
    public void Feed_never_exposes_supplier_cost_or_internal_margin()
    {
        // Structural guarantee: the feed columns are shopper-facing only — no cost/margin/supplier.
        Assert.DoesNotContain(ProductFeed.Columns, c => c.Contains("cost") || c.Contains("margin") || c.Contains("supplier"));

        var feed = ProductFeed.Generate(new FeedSettings(Enabled: true), new[] { Product() }, "https://s")!;
        Assert.DoesNotContain("cost", feed);
        Assert.DoesNotContain("margin", feed);
    }
}
