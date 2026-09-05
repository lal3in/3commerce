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
        group.MapGet("/summary", GetSummary);
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

    /// <summary>
    /// The money preview for the current cart — the ONLY place the storefront learns what promotions
    /// apply, so the promotion algorithm is never re-implemented in TypeScript (ADR-0051, "shown ==
    /// charged"). Anonymous and cookie-keyed exactly like <c>GET /cart</c>, which is left untouched
    /// because many callers depend on its shape (and it deliberately returns the ADD-TIME catalog price).
    /// <para>
    /// Every input is resolved the way checkout resolves it: the offer-resolved effective selling price
    /// per line (approval-gated, ADR-0047/0048), the storefront-wide discount from the projected config,
    /// and the SAME shared PromotionEvaluator. The one difference is shipping: the preview has no carrier
    /// quote yet, so it scores free shipping against the flat fallback rate. That only affects which
    /// promotion WINS in the rare case a free-shipping promotion ties on benefit — the discount figure
    /// shown for the goods is computed identically to the charge.
    /// </para>
    /// <para>
    /// <paramref name="couponCode"/> is what the shopper typed (ADR-0052). It is VALIDATED but never
    /// reserved here: a preview must not consume an allowance, so the returned CouponStatus is advisory
    /// and checkout's atomic reservation remains the authority on the usage limits.
    /// </para>
    /// </summary>
    private static async Task<Ok<CartSummaryResponse>> GetSummary(
        Guid? storefrontId, string? couponCode, HttpContext http, CartService carts, OrderingDbContext db,
        PromotionRedemptionService redemptions, TimeProvider time, CancellationToken ct)
    {
        var userId = UserId(http.User);
        var cart = await carts.GetOrCreateAsync(userId, EnsureCartKey(http), ct);
        var currency = cart.Items.FirstOrDefault()?.Currency ?? StoreCurrency;
        if (cart.Items.Count == 0)
        {
            return TypedResults.Ok(new CartSummaryResponse(0, 0, 0, 0, false, [], currency));
        }

        // Same resolution order as checkout: tenant/storefront from the body-or-header, then the read copies.
        var tenantId = HeaderGuid(http, "X-3C-Tenant-Id") ?? Guid.Parse("00000000-0000-0000-0000-000000000001");
        var storeId = storefrontId ?? HeaderGuid(http, "X-3C-Storefront-Id") ?? tenantId;
        var now = time.GetUtcNow();

        var productIds = cart.Items.Select(i => i.ProductId).Distinct().ToList();
        var offerCopies = await db.OfferCopies.AsNoTracking()
            .Where(o => o.TenantId == tenantId && productIds.Contains(o.ProductId))
            .ToListAsync(ct);
        var offerSupplierIds = offerCopies.Select(o => o.SupplierId).Distinct().ToList();
        // DECISION A (ADR-0048): an unapproved supplier's offer must not set a price here either, or the
        // preview would show a price checkout refuses to charge.
        var approvedSupplierIds = (await db.SupplierApprovalCopies.AsNoTracking()
            .Where(x => x.Approved && offerSupplierIds.Contains(x.SupplierId))
            .Select(x => x.SupplierId)
            .ToListAsync(ct)).ToHashSet();

        var lines = cart.Items.Select(i =>
        {
            var offerPrice = OfferResolution.ResolvePricingOffer(
                offerCopies, tenantId, i.ProductId, i.VariantId, storeId, i.Currency, now, approvedSupplierIds)?.PriceMinor;
            return new PromotionLine(i.ProductId, offerPrice ?? i.UnitPriceMinor, i.Quantity);
        }).ToList();
        var subtotalMinor = lines.Sum(l => l.TotalMinor);

        var promotionCopies = await db.PromotionCopies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Active
                && (p.StorefrontId == null || p.StorefrontId == storeId))
            .ToListAsync(ct);
        // Coupon code (ADR-0052): the SAME validation checkout runs, so the shopper learns here — before
        // paying — whether their code applies and, if not, exactly why. The lookup is by (tenant, code)
        // only: a real code aimed at another store must report "wrong storefront", not "unknown code".
        // Redemption counts are read (not reserved) — a preview never consumes an allowance; checkout's
        // atomic reservation is what actually rations the code.
        var enteredCode = CouponValidator.Normalize(couponCode);
        var (_, couponEvaluation) = await redemptions.ResolveCouponAsync(
            enteredCode, tenantId, storeId, currency, lines,
            // A guest has no identity yet (no email is typed at this point), so only a signed-in shopper's
            // per-customer limit can be reported here; checkout, which HAS the email, refuses the rest.
            PromotionRedemption.CustomerKeyFor(userId, null), now, ct);

        // Only a VALID code unlocks its promotion in the evaluation; an invalid one is reported as a reason
        // and otherwise ignored, so the preview shows the price the shopper would actually be charged.
        var outcome = PromotionEvaluator.Evaluate(
            lines, promotionCopies, tenantId, storeId, currency, CheckoutEndpoints.FlatShippingMinor, now,
            couponEvaluation.IsApplied ? enteredCode : null);

        var discountBps = await db.StorefrontTaxCopies.AsNoTracking()
            .Where(t => t.StorefrontId == storeId)
            .Select(t => (int?)t.DiscountBasisPoints)
            .FirstOrDefaultAsync(ct) ?? 0;
        var storefrontDiscountMinor = discountBps > 0
            ? (long)Math.Round(subtotalMinor * discountBps / 10000.0, MidpointRounding.AwayFromZero)
            : 0L;

        // Same joint cap as checkout: the goods can never be discounted below zero.
        var totalDiscount = Math.Clamp(outcome.DiscountMinor + storefrontDiscountMinor, 0, subtotalMinor);
        var applied = outcome.AppliedPromotionIds
            .Select(id => promotionCopies.First(p => p.PromotionId == id))
            .Select(p => new AppliedPromotionResponse(
                p.PromotionId,
                p.Name,
                // The shopper-facing share of this promotion: its own reward on its own scope base.
                PromotionEvaluator.CandidateFor(
                    lines, p.PromotionId, p.Scope, p.ProductId, p.MinimumAmountMinor, p.MinimumQuantity,
                    p.GrantsFreeShipping, p.PercentOff, p.DiscountAmountMinor, p.Combinable)?.DiscountMinor ?? 0))
            .ToList();

        return TypedResults.Ok(new CartSummaryResponse(
            subtotalMinor, storefrontDiscountMinor, outcome.DiscountMinor,
            subtotalMinor - totalDiscount, outcome.FreeShippingApplied, applied, currency,
            couponEvaluation.Status, enteredCode, couponEvaluation.Name));
    }

    private static Guid? HeaderGuid(HttpContext http, string name) =>
        Guid.TryParse(http.Request.Headers[name].FirstOrDefault(), out var id) ? id : null;

    private static async Task<Results<Ok<CartResponse>, NotFound<string>, Conflict<string>>> AddItem(
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

        // A cart is single-currency: checkout prices, revalidates, and charges in one currency, so a
        // mixed cart would sum unlike units into a wrong total. Reject rather than mix.
        var cartCurrency = cart.Items.FirstOrDefault()?.Currency;
        if (cartCurrency is not null && !string.Equals(cartCurrency, currency, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Conflict($"Cart is in {cartCurrency}; empty it to shop in {currency}.");
        }

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

/// <summary>One promotion the cart currently qualifies for, and what it takes off (ADR-0051).</summary>
public record AppliedPromotionResponse(Guid PromotionId, string Name, long DiscountMinor);

/// <summary>
/// The cart's money preview (ADR-0051). SubtotalMinor is the offer-resolved item value; the two discount
/// figures are reported separately because the storefront-wide discount is a store SETTING, not a
/// promotion; ItemsTotalMinor is what the goods cost after both, jointly capped at the subtotal.
/// </summary>
public record CartSummaryResponse(
    long SubtotalMinor, long StorefrontDiscountMinor, long PromotionDiscountMinor,
    long ItemsTotalMinor, bool FreeShippingApplied,
    List<AppliedPromotionResponse> AppliedPromotions, string Currency,
    // Coupon feedback (ADR-0052), appended with defaults. CouponStatus crosses HTTP as a NUMBER (platform
    // invariant); the storefront maps each member onto its own localized message, so the shopper is told
    // exactly which rule refused the code rather than a blanket "invalid coupon".
    CouponStatus CouponStatus = CouponStatus.None,
    string? CouponCode = null,
    string CouponPromotionName = "");
