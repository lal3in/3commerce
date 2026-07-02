using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.Ordering.Api.Endpoints;

public static class CartEndpoints
{
    public const string CartCookie = "3c_cart";

    /// <summary>Store currency for empty-cart responses (real carts carry the product currency).</summary>
    public static string StoreCurrency { get; set; } = "EUR";

    public static IEndpointRouteBuilder MapCart(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/cart").WithTags("Cart");
        group.MapGet("/", GetCart);
        group.MapPost("/items", AddItem);
        group.MapPut("/items/{productId:guid}", UpdateItem);
        group.MapPut("/items/{productId:guid}/{variantId:guid}", UpdateVariantItem);
        group.MapDelete("/items/{productId:guid}", RemoveItem);
        group.MapDelete("/items/{productId:guid}/{variantId:guid}", RemoveVariantItem);
        return app;
    }

    internal static Guid? UserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out var id) ? id : null;

    internal static string EnsureCartKey(HttpContext http)
    {
        if (http.Request.Cookies.TryGetValue(CartCookie, out var key) && key.Length > 0)
        {
            return key;
        }

        key = Guid.CreateVersion7().ToString("N");
        http.Response.Cookies.Append(CartCookie, key, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(30),
            Path = "/",
        });
        return key;
    }

    private static async Task<Ok<CartResponse>> GetCart(
        HttpContext http, CartService carts, CancellationToken ct)
    {
        var key = EnsureCartKey(http);
        var cart = await carts.GetOrCreateAsync(UserId(http.User), key, ct);
        return TypedResults.Ok(ToResponse(cart));
    }

    private static async Task<Results<Ok<CartResponse>, NotFound<string>>> AddItem(
        AddItemRequest request, HttpContext http, CartService carts, OrderingDbContext db, CancellationToken ct)
    {
        var product = await db.ProductCopies.Include(p => p.Variants).ThenInclude(v => v.Prices).SingleOrDefaultAsync(p => p.ProductId == request.ProductId, ct);
        if (product is null)
        {
            return TypedResults.NotFound("Unknown product.");
        }

        var selected = SelectVariant(product, request.VariantId);
        if (selected is null)
        {
            return TypedResults.NotFound("Unknown variant.");
        }

        // Price in the storefront's currency (tenant-authored per-currency price); if the tenant set
        // no price for this currency the product is not sold there — reject rather than mis-price.
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? selected.Currency : request.Currency!.Trim().ToUpperInvariant();
        var unitPrice = selected.PriceInCurrency(currency);
        if (unitPrice is null)
        {
            return TypedResults.NotFound($"This product is not available in {currency}.");
        }

        var key = EnsureCartKey(http);
        var cart = await carts.GetOrCreateAsync(UserId(http.User), key, ct);
        var item = cart.Items.FirstOrDefault(i => i.ProductId == request.ProductId && i.VariantId == selected.VariantId);
        if (item is null)
        {
            var newItem = new CartItem
            {
                Id = Guid.CreateVersion7(),
                CartId = cart.Id,
                ProductId = product.ProductId,
                VariantId = selected.VariantId,
                VariantSku = selected.Sku,
                Slug = product.Slug,
                Title = product.Title,
                ImageUrl = product.ImageUrl,
                UnitPriceMinor = unitPrice.Value,
                Currency = currency,
                Quantity = request.Quantity,
            };
            cart.Items.Add(newItem);
            db.CartItems.Add(newItem);
        }
        else
        {
            item.Quantity += request.Quantity;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToResponse(cart));
    }

    private static Task<Results<Ok<CartResponse>, NotFound>> UpdateItem(
        Guid productId, UpdateItemRequest request, HttpContext http, CartService carts, OrderingDbContext db, CancellationToken ct) =>
        UpdateLine(productId, null, request, http, carts, db, ct);

    private static Task<Results<Ok<CartResponse>, NotFound>> UpdateVariantItem(
        Guid productId, Guid variantId, UpdateItemRequest request, HttpContext http, CartService carts, OrderingDbContext db, CancellationToken ct) =>
        UpdateLine(productId, variantId, request, http, carts, db, ct);

    private static async Task<Results<Ok<CartResponse>, NotFound>> UpdateLine(
        Guid productId, Guid? variantId, UpdateItemRequest request, HttpContext http, CartService carts, OrderingDbContext db, CancellationToken ct)
    {
        var cart = await carts.GetOrCreateAsync(UserId(http.User), EnsureCartKey(http), ct);
        var item = FindLine(cart, productId, variantId);
        if (item is null)
        {
            return TypedResults.NotFound();
        }

        if (request.Quantity <= 0)
        {
            cart.Items.Remove(item);
            db.CartItems.Remove(item);
        }
        else
        {
            item.Quantity = request.Quantity;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToResponse(cart));
    }

    private static Task<Ok<CartResponse>> RemoveItem(
        Guid productId, HttpContext http, CartService carts, OrderingDbContext db, CancellationToken ct) =>
        RemoveLine(productId, null, http, carts, db, ct);

    private static Task<Ok<CartResponse>> RemoveVariantItem(
        Guid productId, Guid variantId, HttpContext http, CartService carts, OrderingDbContext db, CancellationToken ct) =>
        RemoveLine(productId, variantId, http, carts, db, ct);

    private static async Task<Ok<CartResponse>> RemoveLine(
        Guid productId, Guid? variantId, HttpContext http, CartService carts, OrderingDbContext db, CancellationToken ct)
    {
        var cart = await carts.GetOrCreateAsync(UserId(http.User), EnsureCartKey(http), ct);
        var item = FindLine(cart, productId, variantId);
        if (item is not null)
        {
            cart.Items.Remove(item);
            db.CartItems.Remove(item);
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.Ok(ToResponse(cart));
    }

    private static ProductVariantCopy? SelectVariant(ProductCopy product, Guid? variantId) =>
        variantId is { } id
            ? product.Variants.FirstOrDefault(v => v.VariantId == id)
            : product.Variants.OrderBy(v => v.PriceMinor).FirstOrDefault()
                ?? new ProductVariantCopy
                {
                    VariantId = Guid.Empty,
                    ProductId = product.ProductId,
                    Sku = "default",
                    PriceMinor = product.MinPriceMinor,
                    Currency = product.Currency,
                    StockQuantity = 0,
                };

    private static CartItem? FindLine(Cart cart, Guid productId, Guid? variantId) =>
        cart.Items.FirstOrDefault(i => i.ProductId == productId && (variantId == null || i.VariantId == variantId));

    private static CartResponse ToResponse(Cart cart)
    {
        var items = cart.Items
            .Select(i => new CartItemResponse(i.ProductId, i.VariantId, i.VariantSku, i.Slug, i.Title, i.ImageUrl, i.UnitPriceMinor, i.Currency, i.Quantity))
            .ToList();
        var subtotal = items.Sum(i => i.UnitPriceMinor * i.Quantity);
        var currency = items.FirstOrDefault()?.Currency ?? StoreCurrency;
        return new CartResponse(cart.Id, items, subtotal, currency);
    }
}

public record AddItemRequest([property: Required] Guid ProductId, Guid? VariantId, [property: Range(1, 99)] int Quantity, string? Currency = null);
public record UpdateItemRequest([property: Range(0, 99)] int Quantity);
public record CartItemResponse(Guid ProductId, Guid? VariantId, string? VariantSku, string Slug, string Title, string? ImageUrl, long UnitPriceMinor, string Currency, int Quantity);
public record CartResponse(Guid CartId, List<CartItemResponse> Items, long SubtotalMinor, string Currency);
