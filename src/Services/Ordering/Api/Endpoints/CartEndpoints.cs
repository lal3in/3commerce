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
        group.MapDelete("/items/{productId:guid}", RemoveItem);
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
        var product = await db.ProductCopies.FindAsync([request.ProductId], ct);
        if (product is null)
        {
            return TypedResults.NotFound("Unknown product.");
        }

        var key = EnsureCartKey(http);
        var cart = await carts.GetOrCreateAsync(UserId(http.User), key, ct);
        var item = cart.Items.FirstOrDefault(i => i.ProductId == request.ProductId);
        if (item is null)
        {
            // Explicit Add marks the row Added (INSERT); collection-add can mis-infer Modified.
            var newItem = new CartItem
            {
                Id = Guid.CreateVersion7(),
                CartId = cart.Id,
                ProductId = product.ProductId,
                Slug = product.Slug,
                Title = product.Title,
                ImageUrl = product.ImageUrl,
                UnitPriceMinor = product.MinPriceMinor,
                Currency = product.Currency,
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

    private static async Task<Results<Ok<CartResponse>, NotFound>> UpdateItem(
        Guid productId, UpdateItemRequest request, HttpContext http, CartService carts, OrderingDbContext db, CancellationToken ct)
    {
        var cart = await carts.GetOrCreateAsync(UserId(http.User), EnsureCartKey(http), ct);
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId);
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

    private static async Task<Ok<CartResponse>> RemoveItem(
        Guid productId, HttpContext http, CartService carts, OrderingDbContext db, CancellationToken ct)
    {
        var cart = await carts.GetOrCreateAsync(UserId(http.User), EnsureCartKey(http), ct);
        var item = cart.Items.FirstOrDefault(i => i.ProductId == productId);
        if (item is not null)
        {
            cart.Items.Remove(item);
            db.CartItems.Remove(item);
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.Ok(ToResponse(cart));
    }

    private static CartResponse ToResponse(Cart cart)
    {
        var items = cart.Items
            .Select(i => new CartItemResponse(i.ProductId, i.Slug, i.Title, i.ImageUrl, i.UnitPriceMinor, i.Currency, i.Quantity))
            .ToList();
        var subtotal = items.Sum(i => i.UnitPriceMinor * i.Quantity);
        var currency = items.FirstOrDefault()?.Currency ?? StoreCurrency;
        return new CartResponse(cart.Id, items, subtotal, currency);
    }
}

public record AddItemRequest([property: Required] Guid ProductId, [property: Range(1, 99)] int Quantity);
public record UpdateItemRequest([property: Range(0, 99)] int Quantity);
public record CartItemResponse(Guid ProductId, string Slug, string Title, string? ImageUrl, long UnitPriceMinor, string Currency, int Quantity);
public record CartResponse(Guid CartId, List<CartItemResponse> Items, long SubtotalMinor, string Currency);
