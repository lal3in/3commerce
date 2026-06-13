using System.ComponentModel.DataAnnotations;
using MassTransit;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.Ordering.Api.Endpoints;

public static class CheckoutEndpoints
{
    /// <summary>Flat shipping for v1 (ADR-0015: no carrier rates yet). Minor units.</summary>
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
            var current = await db.ProductCopies.FindAsync([item.ProductId], ct);
            if (current is not null && current.MinPriceMinor != item.UnitPriceMinor)
            {
                item.UnitPriceMinor = current.MinPriceMinor;
                priceChanged = true;
            }
        }

        var currency = cart.Items[0].Currency;
        var subtotal = cart.Items.Sum(i => i.UnitPriceMinor * i.Quantity);
        var netMinor = subtotal + FlatShippingMinor;

        if (priceChanged)
        {
            await db.SaveChangesAsync(ct);
            return TypedResults.Conflict(new CheckoutResponse(
                Guid.Empty, null, netMinor, 0, netMinor, currency, "Prices changed; review your cart."));
        }

        var orderId = Guid.CreateVersion7();
        var idempotencyKey = orderId.ToString();

        AuthorizePaymentResult intent;
        try
        {
            var response = await authorize.GetResponse<AuthorizePaymentResult>(
                new AuthorizePayment(orderId, netMinor, currency, idempotencyKey), ct);
            intent = response.Message;
        }
        catch (RequestTimeoutException)
        {
            return TypedResults.BadRequest("Payment service unavailable; please retry.");
        }

        var order = new Order
        {
            Id = orderId,
            UserId = userId,
            Email = userId is null ? request.Email : request.Email,
            Status = OrderStatus.AwaitingPayment,
            NetMinor = subtotal,
            ShippingMinor = FlatShippingMinor,
            TaxMinor = intent.TaxMinor,
            GrossMinor = intent.GrossMinor,
            Currency = currency,
            PaymentIntentId = intent.PaymentIntentId,
            ShipName = request.ShippingAddress.Name,
            ShipLine1 = request.ShippingAddress.Line1,
            ShipCity = request.ShippingAddress.City,
            ShipPostcode = request.ShippingAddress.Postcode,
            ShipCountry = request.ShippingAddress.Country,
            CreatedAt = time.GetUtcNow(),
            Lines = cart.Items.Select(i => new OrderLine
            {
                Id = Guid.CreateVersion7(),
                OrderId = orderId,
                ProductId = i.ProductId,
                Title = i.Title,
                UnitPriceMinor = i.UnitPriceMinor,
                Quantity = i.Quantity,
                FulfillmentSource = FulfillmentSource.Unassigned,
            }).ToList(),
        };
        db.Orders.Add(order);

        // Start the saga; clearing the cart and publishing commit in one transaction (outbox).
        await publisher.Publish(new CartSubmitted(orderId, intent.PaymentIntentId, intent.GrossMinor, currency, order.Email), ct);
        db.CartItems.RemoveRange(cart.Items);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/orders/{orderId}", new CheckoutResponse(
            orderId, intent.ClientSecret, subtotal + FlatShippingMinor, intent.TaxMinor, intent.GrossMinor, currency, null));
    }
}

public record AddressRequest(
    [property: Required] string Name,
    [property: Required] string Line1,
    [property: Required] string City,
    [property: Required] string Postcode,
    [property: Required, StringLength(2, MinimumLength = 2)] string Country);

public record CheckoutRequest([property: Required, EmailAddress] string Email, [property: Required] AddressRequest ShippingAddress);

public record CheckoutResponse(Guid OrderId, string? ClientSecret, long NetMinor, long TaxMinor, long GrossMinor, string Currency, string? Message);
