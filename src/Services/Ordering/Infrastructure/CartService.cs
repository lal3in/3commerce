using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Ordering.Domain;

namespace ThreeCommerce.Ordering.Infrastructure;

/// <summary>
/// Resolves the active cart for a request (user-owned takes precedence; anonymous by
/// cookie key), merging an anonymous cart into the user's on first authenticated access.
/// </summary>
public sealed class CartService(OrderingDbContext db, TimeProvider time)
{
    public async Task<Cart> GetOrCreateAsync(Guid? userId, string? cartKey, CancellationToken ct)
    {
        if (userId is { } uid)
        {
            var userCart = await Load(c => c.UserId == uid, ct);
            if (userCart is null)
            {
                userCart = new Cart { Id = Guid.CreateVersion7(), UserId = uid, UpdatedAt = time.GetUtcNow() };
                db.Carts.Add(userCart);
            }

            if (!string.IsNullOrEmpty(cartKey))
            {
                var anon = await Load(c => c.CartKey == cartKey && c.UserId == null, ct);
                if (anon is not null && anon.Id != userCart.Id)
                {
                    MergeInto(anon, userCart);
                    db.CartItems.RemoveRange(anon.Items);
                    db.Carts.Remove(anon);
                }
            }

            await db.SaveChangesAsync(ct);
            return userCart;
        }

        var key = cartKey ?? string.Empty;
        var cart = await Load(c => c.CartKey == key && c.UserId == null, ct);
        if (cart is null)
        {
            cart = new Cart { Id = Guid.CreateVersion7(), CartKey = key, UpdatedAt = time.GetUtcNow() };
            db.Carts.Add(cart);
            await db.SaveChangesAsync(ct);
        }

        return cart;
    }

    private Task<Cart?> Load(Expression<Func<Cart, bool>> predicate, CancellationToken ct) =>
        db.Carts.Include(c => c.Items).FirstOrDefaultAsync(predicate, ct);

    private void MergeInto(Cart from, Cart into)
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
                var moved = new CartItem
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
                };
                into.Items.Add(moved);
                db.CartItems.Add(moved);
            }
        }
    }
}
