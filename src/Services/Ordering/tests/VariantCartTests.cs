using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.Ordering.Tests;

public class VariantCartTests
{
    [Fact]
    public void Cart_merge_keys_lines_by_product_and_variant()
    {
        var into = new Cart { Id = Guid.CreateVersion7(), UserId = Guid.CreateVersion7(), UpdatedAt = DateTimeOffset.UtcNow };
        var productId = Guid.CreateVersion7();
        var red = Guid.CreateVersion7();
        var blue = Guid.CreateVersion7();
        into.Items.Add(Line(into.Id, productId, red, "RED", quantity: 1));
        var from = new Cart { Id = Guid.CreateVersion7(), CartKey = "anon", UpdatedAt = DateTimeOffset.UtcNow };
        from.Items.Add(Line(from.Id, productId, red, "RED", quantity: 2));
        from.Items.Add(Line(from.Id, productId, blue, "BLUE", quantity: 1));

        CartMerge.ForTests(from, into);

        Assert.Equal(2, into.Items.Count);
        Assert.Equal(3, into.Items.Single(i => i.VariantId == red).Quantity);
        Assert.Equal(1, into.Items.Single(i => i.VariantId == blue).Quantity);
    }

    [Fact]
    public void Checkout_attempt_preserves_variant_snapshot_on_order_lines()
    {
        var variantId = Guid.CreateVersion7();
        var attempt = new CheckoutAttempt
        {
            Id = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            StorefrontId = Guid.CreateVersion7(),
            Email = "buyer@example.test",
            NetMinor = 1000,
            ShippingMinor = 0,
            TaxMinor = 0,
            GrossMinor = 1000,
            Currency = "AUD",
            PaymentIntentId = "pi_fake",
            ShipName = "Buyer",
            ShipLine1 = "1 Street",
            ShipCity = "Sydney",
            ShipPostcode = "2000",
            ShipCountry = "AU",
            CreatedAt = DateTimeOffset.UtcNow,
            Lines =
            [
                new CheckoutAttemptLine
                {
                    Id = Guid.CreateVersion7(),
                    CheckoutAttemptId = Guid.CreateVersion7(),
                    ProductId = Guid.CreateVersion7(),
                    VariantId = variantId,
                    VariantSku = "SKU-RED",
                    Title = "Product",
                    UnitPriceMinor = 1000,
                    Quantity = 1,
                },
            ],
        };

        var order = attempt.ToOrder(1000, DateTimeOffset.UtcNow);

        Assert.Equal(variantId, order.Lines.Single().VariantId);
        Assert.Equal("SKU-RED", order.Lines.Single().VariantSku);
    }

    private static CartItem Line(Guid cartId, Guid productId, Guid variantId, string sku, int quantity) => new()
    {
        Id = Guid.CreateVersion7(),
        CartId = cartId,
        ProductId = productId,
        VariantId = variantId,
        VariantSku = sku,
        Slug = "product",
        Title = "Product",
        UnitPriceMinor = 1000,
        Currency = "AUD",
        Quantity = quantity,
    };
}

internal static class CartMerge
{
    public static void ForTests(Cart from, Cart into)
    {
        foreach (var item in from.Items)
        {
            var existing = into.Items.FirstOrDefault(i => i.ProductId == item.ProductId && i.VariantId == item.VariantId);
            if (existing is not null)
            {
                existing.Quantity += item.Quantity;
            }
            else
            {
                into.Items.Add(new CartItem
                {
                    Id = Guid.CreateVersion7(),
                    CartId = into.Id,
                    ProductId = item.ProductId,
                    VariantId = item.VariantId,
                    VariantSku = item.VariantSku,
                    Slug = item.Slug,
                    Title = item.Title,
                    ImageUrl = item.ImageUrl,
                    UnitPriceMinor = item.UnitPriceMinor,
                    Currency = item.Currency,
                    Quantity = item.Quantity,
                });
            }
        }
    }
}
