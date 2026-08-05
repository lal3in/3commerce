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
        IConfiguration config,
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
                ? await db.ProductVariantCopies.Include(v => v.Prices).FirstOrDefaultAsync(v => v.VariantId == variantId, ct)
                : null;
            // Revalidate against the tenant's current price in the cart item's currency (per-currency pricing).
            var currentPrice = current?.PriceInCurrency(item.Currency) ?? (await db.ProductCopies.FindAsync([item.ProductId], ct))?.MinPriceMinor;
            if (currentPrice is { } price && price != item.UnitPriceMinor)
            {
                item.UnitPriceMinor = price;
                priceChanged = true;
            }
        }

        var currency = cart.Items[0].Currency;
        // Carts are single-currency (guarded at add + merge); reject legacy/mixed data instead of
        // summing unlike units into a wrong charge.
        if (cart.Items.Any(i => !string.Equals(i.Currency, currency, StringComparison.OrdinalIgnoreCase)))
        {
            return TypedResults.BadRequest("Cart contains items in different currencies; empty it and re-add items.");
        }

        var subtotal = cart.Items.Sum(i => i.UnitPriceMinor * i.Quantity);
        var discountMinor = 0L;

        // Resolve the cart's fulfilment up front (from the OfferCopy read model) so shipping can be gated:
        // only a cart with at least one shippable line is charged shipping — a non-shippable (e.g. digital/
        // service/usage) order ships nothing and must not pay shipping (mt4 / ADR-0028). Which product types
        // ship is the tenant's configurable ProductType policy (projected as ProductTypeShippingPolicyCopy);
        // when no policy copy or a line's product type is unknown, it falls back to the fulfilment-type gate.
        // offerCopies is reused below for the per-line attempt build, so it's loaded once here.
        var checkoutTenantId = HeaderGuid(http, "X-3C-Tenant-Id") ?? Guid.Parse("00000000-0000-0000-0000-000000000001");
        var cartProductIds = cart.Items.Select(i => i.ProductId).Distinct().ToList();
        var offerCopies = await db.OfferCopies.AsNoTracking()
            .Where(o => o.TenantId == checkoutTenantId && cartProductIds.Contains(o.ProductId))
            .ToListAsync(ct);
        var shippingPolicy = await db.ProductTypeShippingPolicyCopies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == checkoutTenantId, ct);
        var anyShippable = cart.Items.Any(i => LineRequiresShipping(
            OfferResolution.ResolveOffer(offerCopies, checkoutTenantId, i.ProductId, i.VariantId), shippingPolicy));

        var requestedShippingMinor = request.SelectedShippingAmountMinor ?? FlatShippingMinor;
        if (requestedShippingMinor < 0)
        {
            return TypedResults.BadRequest("Selected shipping amount cannot be negative.");
        }

        // Nothing shippable in the cart → no shipping charge, whatever the client sent.
        var shippingMinor = anyShippable ? requestedShippingMinor : 0L;

        if (request.SelectedShippingAmountMinor is not null &&
            (string.IsNullOrWhiteSpace(request.SelectedShippingService) || request.SelectedShippingExpiresAt is null))
        {
            return TypedResults.BadRequest("Selected shipping requires a service and expiry.");
        }

        if (request.SelectedShippingExpiresAt is { } expiresAt && expiresAt <= time.GetUtcNow())
        {
            return TypedResults.BadRequest("Selected shipping quote has expired; refresh shipping options.");
        }

        // Storefront tax (ADR-0008 projection, ADR-0038 semantics): rate + inclusiveness resolved by
        // the cart's currency. Inclusive regimes (AU GST / EU VAT): the tenant's shelf prices already
        // CONTAIN the tax — the shopper pays exactly the listed amount and the contained portion is
        // reported informationally. Exclusive regimes (US sales tax): tax is added on goods + shipping.
        var taxConfig = await db.StorefrontTaxCopies
            .Where(t => t.IsLive && t.Currency == currency)
            .OrderByDescending(t => t.TaxRateBasisPoints)
            .Select(t => new { t.TaxRateBasisPoints, t.TaxInclusive })
            .FirstOrDefaultAsync(ct);
        var taxBps = taxConfig?.TaxRateBasisPoints ?? 0;
        var baseMinor = subtotal - discountMinor + shippingMinor;
        long taxMinor, netMinor;
        if (taxConfig?.TaxInclusive == true)
        {
            taxMinor = (long)Math.Round(baseMinor * taxBps / (10000.0 + taxBps), MidpointRounding.AwayFromZero);
            netMinor = baseMinor; // listed price IS the charge
        }
        else
        {
            taxMinor = (long)Math.Round(baseMinor * taxBps / 10000.0, MidpointRounding.AwayFromZero);
            netMinor = baseMinor + taxMinor;
        }

        if (priceChanged)
        {
            await db.SaveChangesAsync(ct);
            return TypedResults.Conflict(new CheckoutResponse(
                Guid.Empty, null, subtotal, discountMinor, shippingMinor, taxMinor, netMinor, currency, "Prices changed; review your cart."));
        }

        var paymentOption = NormalizePaymentOption(request.PaymentOption);
        var paymentInstrumentSummary = PaymentInstrumentSummary(paymentOption, request.PaymentInstrumentSummary);

        var orderId = Guid.CreateVersion7();
        var idempotencyKey = orderId.ToString();
        // Resolve tenant/storefront up front (gateway-trusted headers) so the storefront rides the
        // AuthorizePayment request → Payment, letting the sale post to the storefront's own accounts.
        var tenantId = checkoutTenantId; // resolved up front for the shipping gate; reused here
        // The first-party storefront app resolves the exact active store and sends it in the body, so it
        // wins for attribution; the gateway's host-derived header is only a fallback (it collapses all
        // path-based demo stores to one configured default), then the tenant default.
        var storefrontId = request.StorefrontId ?? HeaderGuid(http, "X-3C-Storefront-Id") ?? tenantId;

        // Every order must belong to a real storefront (rev_5). The gateway stamps its synthetic
        // DefaultStorefrontId when a request carries no resolvable store context (no /{slug}, no
        // domain) — that is not a real storefront (no published catalog, no ledger accounts), so
        // reject rather than book an orphan order that lands on the shared revenue.sales /
        // liability.tax_collected accounts. The first-party app always sends the active store.
        var defaultStorefrontId = Guid.TryParse(config["Tenancy:DefaultStorefrontId"], out var d)
            ? d
            : Guid.Parse("00000000-0000-0000-0000-000000000101");
        if (storefrontId == defaultStorefrontId)
        {
            return TypedResults.BadRequest("A storefront is required to check out — no store context was resolved.");
        }

        AuthorizePaymentResult intent;
        try
        {
            var response = await authorize.GetResponse<AuthorizePaymentResult>(
                new AuthorizePayment(orderId, netMinor, currency, idempotencyKey, userId, request.SavedPaymentMethodId, request.SavePaymentMethod, request.ShippingAddress.Country, paymentOption, storefrontId, taxMinor, shippingMinor), ct);
            intent = response.Message;
        }
        catch (RequestTimeoutException)
        {
            return TypedResults.BadRequest("Payment service unavailable; please retry.");
        }

        var now = time.GetUtcNow();

        // Each line's fulfilment is resolved from offerCopies (loaded up front for the shipping gate):
        // the OfferCopy read model is fed by Catalog's OfferChanged events. No offer → Unassigned.
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
            // Ordering owns storefront tax (ADR-0008 projection); Payments' flat-rate seam is unused here.
            TaxMinor = taxMinor,
            GrossMinor = intent.GrossMinor,
            Currency = currency,
            PaymentIntentId = intent.PaymentIntentId,
            PaymentOption = paymentOption,
            PaymentInstrumentSummary = paymentInstrumentSummary,
            PaymentProvider = "Stripe",
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
            orderId, intent.ClientSecret, subtotal, discountMinor, shippingMinor, taxMinor, intent.GrossMinor, currency, null));
    }

    private static Guid? HeaderGuid(HttpContext http, string name) =>
        Guid.TryParse(http.Request.Headers[name].FirstOrDefault(), out var id) ? id : null;

    // Does this cart line ship? The tenant's ProductType policy decides when we know the line's product
    // type and a policy copy exists; otherwise we fall back to the fulfilment-type gate (the behaviour
    // before the policy existed, and the answer for a line with no matching offer — default shippable).
    private static bool LineRequiresShipping(OfferCopy? offer, ProductTypeShippingPolicyCopy? policy)
    {
        if (offer is null)
        {
            return FulfilmentType.Unassigned.RequiresShipping();
        }

        if (policy is not null && offer.ProductType != default)
        {
            return policy.RequiresShipping(offer.ProductType);
        }

        return offer.FulfilmentType.RequiresShipping();
    }

    private static string NormalizePaymentOption(string? option)
    {
        var normalized = (option ?? "CreditCard").Trim();
        return normalized switch
        {
            // Pass through every method Payments knows how to route (PaymentMethodKindMapper / ADR-0039):
            // wallets settle through the card PSP, while PayPal / Afterpay / Polar are standalone PSPs and
            // must reach Payments as-is — dropping them to CreditCard mis-settled them to the card acquirer.
            "Stripe" or "CreditCard" or "ApplePay" or "GooglePay" or "PayPal" or "Afterpay" or "Polar" => normalized,
            _ => "CreditCard",
        };
    }

    private static string? PaymentInstrumentSummary(string option, string? summary)
    {
        var value = string.IsNullOrWhiteSpace(summary) ? option : summary.Trim();
        return value.Length <= 120 ? value : value[..120];
    }
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
    string? PaymentOption = null,
    string? PaymentInstrumentSummary = null,
    string? SelectedShippingService = null,
    long? SelectedShippingAmountMinor = null,
    DateTimeOffset? SelectedShippingExpiresAt = null,
    // The active storefront (phase 2). The gateway derives the trusted X-3C-Storefront-Id from the host,
    // which can't distinguish path-based demo storefronts, so the first-party storefront app also sends
    // it here for per-storefront order/ledger attribution. Header wins when present.
    Guid? StorefrontId = null);

public record CheckoutResponse(Guid OrderId, string? ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor, long GrossMinor, string Currency, string? Message);
