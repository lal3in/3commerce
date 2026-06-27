using System.ComponentModel.DataAnnotations;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.Ordering.Api.Endpoints;

public static class CheckoutEndpoints
{
    /// <summary>Fallback shipping for clients that have not selected a Fulfillment quote yet. Minor units.</summary>
    private const long FlatShippingMinor = 499;

    public static IEndpointRouteBuilder MapCheckout(this IEndpointRouteBuilder app)
    {
        app.MapPost("/checkout", Checkout).WithTags("Checkout");
        return app;
    }

    /// <summary>
    /// Returns 201 once the payment intent exists — never blocks on the saga (api.md §3).
    /// Requests the intent synchronously via RequestClient; the saga owns the async remainder.
    /// </summary>
    private static async Task<Results<Created<CheckoutResponse>, BadRequest<string>, Conflict<CheckoutResponse>>> Checkout(
        CheckoutRequest request,
        HttpContext http,
        CartService carts,
        OrderingDbContext db,
        IRequestClient<AuthorizePayment> authorize,
        IPublishEndpoint publisher,
        TimeProvider time,
        CancellationToken ct)
    {
        var userId = CartEndpoints.UserId(http.User);
        var cartKey = CartEndpoints.EnsureCartKey(http);
        var cart = await carts.GetOrCreateAsync(userId, cartKey, ct);

        if (cart.Items.Count == 0)
        {
            return TypedResults.BadRequest("Cart is empty.");
        }

        // Re-validate prices against the current ProductCopy (plan edge case: price drift → 409).
        var priceChanged = false;
        foreach (var item in cart.Items)
        {
            var current = item.VariantId is { } variantId
                ? await db.ProductVariantCopies.FindAsync([variantId], ct)
                : null;
            var currentPrice = current?.PriceMinor ?? (await db.ProductCopies.FindAsync([item.ProductId], ct))?.MinPriceMinor;
            if (currentPrice is { } price && price != item.UnitPriceMinor)
            {
                item.UnitPriceMinor = price;
                priceChanged = true;
            }
        }

        var currency = cart.Items[0].Currency;
        var subtotal = cart.Items.Sum(i => i.UnitPriceMinor * i.Quantity);
        var discountMinor = 0L;
        var shippingMinor = request.SelectedShippingAmountMinor ?? FlatShippingMinor;
        if (shippingMinor < 0)
        {
            return TypedResults.BadRequest("Selected shipping amount cannot be negative.");
        }

        if (request.SelectedShippingAmountMinor is not null &&
            (string.IsNullOrWhiteSpace(request.SelectedShippingService) || request.SelectedShippingExpiresAt is null))
        {
            return TypedResults.BadRequest("Selected shipping requires a service and expiry.");
        }

        if (request.SelectedShippingExpiresAt is { } expiresAt && expiresAt <= time.GetUtcNow())
        {
            return TypedResults.BadRequest("Selected shipping quote has expired; refresh shipping options.");
        }

        var netMinor = subtotal - discountMinor + shippingMinor;

        if (priceChanged)
        {
            await db.SaveChangesAsync(ct);
            return TypedResults.Conflict(new CheckoutResponse(
                Guid.Empty, null, subtotal, discountMinor, shippingMinor, 0, netMinor, currency, "Prices changed; review your cart."));
        }

        var orderId = Guid.CreateVersion7();
        var idempotencyKey = orderId.ToString();

        AuthorizePaymentResult intent;
        try
        {
            var response = await authorize.GetResponse<AuthorizePaymentResult>(
                new AuthorizePayment(orderId, netMinor, currency, idempotencyKey, userId, request.SavedPaymentMethodId, request.SavePaymentMethod, request.ShippingAddress.Country), ct);
            intent = response.Message;
        }
        catch (RequestTimeoutException)
        {
            return TypedResults.BadRequest("Payment service unavailable; please retry.");
        }

        var now = time.GetUtcNow();
        var tenantId = HeaderGuid(http, "X-3C-Tenant-Id") ?? Guid.Parse("00000000-0000-0000-0000-000000000001");
        var storefrontId = HeaderGuid(http, "X-3C-Storefront-Id") ?? tenantId;

        // Resolve each line's fulfilment from its offer (ADR-0028 / mt7_1-lite): the OfferCopy read
        // model is fed by Catalog's OfferChanged events. No offer → Unassigned (no shipment/inventory).
        var productIds = cart.Items.Select(i => i.ProductId).Distinct().ToList();
        var offerCopies = await db.OfferCopies.AsNoTracking()
            .Where(o => o.TenantId == tenantId && productIds.Contains(o.ProductId))
            .ToListAsync(ct);

        var attempt = new CheckoutAttempt
        {
            Id = orderId,
            TenantId = tenantId,
            StorefrontId = storefrontId,
            UserId = userId,
            Email = request.Email,
            Status = CheckoutAttemptStatus.AwaitingPayment,
            NetMinor = subtotal,
            ShippingMinor = shippingMinor,
            DiscountMinor = discountMinor,
            TaxMinor = intent.TaxMinor,
            GrossMinor = intent.GrossMinor,
            Currency = currency,
            PaymentIntentId = intent.PaymentIntentId,
            CampaignRef = request.CampaignRef,
            ShipName = request.ShippingAddress.Name,
            ShipLine1 = request.ShippingAddress.Line1,
            ShipCity = request.ShippingAddress.City,
            ShipPostcode = request.ShippingAddress.Postcode,
            ShipCountry = request.ShippingAddress.Country,
            CreatedAt = now,
            Lines = cart.Items.Select(i =>
            {
                var offer = OfferResolution.ResolveOffer(offerCopies, tenantId, i.ProductId, i.VariantId);
                return new CheckoutAttemptLine
                {
                    Id = Guid.CreateVersion7(),
                    CheckoutAttemptId = orderId,
                    ProductId = i.ProductId,
                    VariantId = i.VariantId,
                    VariantSku = i.VariantSku,
                    Title = i.Title,
                    UnitPriceMinor = i.UnitPriceMinor,
                    DiscountMinor = 0,
                    Quantity = i.Quantity,
                    FulfilmentType = offer?.FulfilmentType ?? FulfilmentType.Unassigned,
                    SupplierId = offer?.SupplierId,
                    BillingMode = offer?.BillingMode ?? BillingMode.OneTime,
                    BillingPeriod = offer?.BillingPeriod ?? BillingPeriod.Once,
                };
            }).ToList(),
        };
        db.CheckoutAttempts.Add(attempt);

        // Start the saga; clearing the cart and publishing commit in one transaction (outbox).
        await publisher.Publish(new CartSubmitted(orderId, intent.PaymentIntentId, intent.GrossMinor, currency, attempt.Email), ct);
        db.CartItems.RemoveRange(cart.Items);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/orders/{orderId}", new CheckoutResponse(
            orderId, intent.ClientSecret, subtotal, discountMinor, shippingMinor, intent.TaxMinor, intent.GrossMinor, currency, null));
    }

    private static Guid? HeaderGuid(HttpContext http, string name) =>
        Guid.TryParse(http.Request.Headers[name].FirstOrDefault(), out var id) ? id : null;
}

public record AddressRequest(
    [property: Required] string Name,
    [property: Required] string Line1,
    [property: Required] string City,
    [property: Required] string Postcode,
    [property: Required, StringLength(2, MinimumLength = 2)] string Country);

public record CheckoutRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] AddressRequest ShippingAddress,
    string? CampaignRef = null,
    Guid? SavedPaymentMethodId = null,
    bool SavePaymentMethod = false,
    string? SelectedShippingService = null,
    long? SelectedShippingAmountMinor = null,
    DateTimeOffset? SelectedShippingExpiresAt = null);

public record CheckoutResponse(Guid OrderId, string? ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor, long GrossMinor, string Currency, string? Message);
