using System.Text;

namespace ThreeCommerce.Marketing.Domain;

public enum FeedFormat { Csv = 1 }

/// <summary>Per-storefront feed toggle + format (mt5_9). Off by default — feeds are opt-in.</summary>
public sealed record FeedSettings(bool Enabled, FeedFormat Format = FeedFormat.Csv);

/// <summary>
/// A product as seen by feed generation (mt5_9). Note what is ABSENT: there is no supplier cost or
/// internal margin field, so a feed structurally cannot leak them (mt5_9 GOTCHA).
/// </summary>
public sealed record FeedProduct(
    string Id,
    string Title,
    string Description,
    string Slug,
    string? ImageUrl,
    long PriceMinor,
    string Currency,
    bool InStock,
    string? Brand,
    bool IsPublished,
    bool IsPublic);

/// <summary>A Google-Merchant-style feed row (mt5_9).</summary>
public sealed record FeedItem(
    string Id, string Title, string Description, string Link, string? ImageLink, string Availability, string Price, string? Brand, string Condition);

/// <summary>
/// Generates a toggleable per-storefront product feed (mt5_9). Only public + published, in-catalog,
/// priced products are eligible; each row carries shopper-facing offer metadata and never any cost.
/// </summary>
public static class ProductFeed
{
    public static readonly IReadOnlyList<string> Columns =
        ["id", "title", "description", "link", "image_link", "availability", "price", "brand", "condition"];

    public static bool IsEligible(FeedProduct product) =>
        product.IsPublished && product.IsPublic && product.PriceMinor > 0;

    public static FeedItem ToItem(FeedProduct product, string baseUrl) => new(
        product.Id,
        product.Title,
        product.Description,
        $"{baseUrl.TrimEnd('/')}/products/{product.Slug}",
        product.ImageUrl,
        product.InStock ? "in_stock" : "out_of_stock",
        $"{product.PriceMinor / 100m:0.00} {product.Currency}",
        product.Brand,
        "new");

    /// <summary>Generate the feed, or null when the storefront has feeds disabled (mt5_9).</summary>
    public static string? Generate(FeedSettings settings, IEnumerable<FeedProduct> products, string baseUrl)
    {
        if (!settings.Enabled)
        {
            return null;
        }

        var items = products.Where(IsEligible).Select(p => ToItem(p, baseUrl));

        var builder = new StringBuilder();
        builder.Append(string.Join(',', Columns)).Append("\r\n");
        foreach (var item in items)
        {
            builder.Append(string.Join(',', new[]
            {
                item.Id, item.Title, item.Description, item.Link, item.ImageLink ?? string.Empty,
                item.Availability, item.Price, item.Brand ?? string.Empty, item.Condition,
            }.Select(Escape))).Append("\r\n");
        }

        return builder.ToString();
    }

    private static string Escape(string field) =>
        field.AsSpan().IndexOfAny(",\"\n\r") >= 0 ? $"\"{field.Replace("\"", "\"\"")}\"" : field;
}
