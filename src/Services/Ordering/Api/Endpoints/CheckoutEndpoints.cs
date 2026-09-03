using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
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
    internal const long FlatShippingMinor = 499;

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

        // Resolve tenant + storefront + the offer read model UP FRONT: an active, in-window offer for this
        // storefront (offer-as-price, ADR-0028) sets the CHARGED price, so it must be known before prices are
        // revalidated below. The storefront also gates shipping and rides AuthorizePayment for attribution.
        var checkoutTenantId = HeaderGuid(http, "X-3C-Tenant-Id") ?? Guid.Parse("00000000-0000-0000-0000-000000000001");
        var tenantId = checkoutTenantId;
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

        // offerCopies is reused for the shipping gate, the offer price override, and the per-line attempt build.
        var cartProductIds = cart.Items.Select(i => i.ProductId).Distinct().ToList();
        var offerCopies = await db.OfferCopies.AsNoTracking()
            .Where(o => o.TenantId == checkoutTenantId && cartProductIds.Contains(o.ProductId))
            .ToListAsync(ct);
        // Threshold promotions (ADR-0051): the tenant's active promotions for this storefront (or all
        // storefronts), read from the local projection — checkout never queries Catalog (ADR-0008).
        // Currency, window and threshold are all decided by the shared PromotionEvaluator below.
        var promotionCopies = await db.PromotionCopies.AsNoTracking()
            .Where(p => p.TenantId == checkoutTenantId && p.Active
                && (p.StorefrontId == null || p.StorefrontId == storefrontId))
            .ToListAsync(ct);
        var now = time.GetUtcNow();

        // Approval-gated availability (DECISION A, strict): an unapproved supplier's offer never counts —
        // not for pricing, not for fulfilment, not at checkout. Load which of the offers' suppliers are
        // approved (SupplierApprovalCopy, fed by Entity's SupplierApprovalChanged), then gate every
        // resolution below on that set. A line whose ONLY covering offers are unapproved has no valid
        // supply and is rejected. Offerless products are unaffected (no supplier to gate — scope guard).
        var offerSupplierIds = offerCopies.Select(o => o.SupplierId).Distinct().ToList();
        var approvedSupplierIds = (await db.SupplierApprovalCopies.AsNoTracking()
            .Where(s => s.Approved && offerSupplierIds.Contains(s.SupplierId))
            .Select(s => s.SupplierId)
            .ToListAsync(ct)).ToHashSet();

        // Block a line that HAS a covering offer but none from an approved supplier (its only supply is
        // unapproved). A line with no offer at all resolves to null in both and keeps catalog behaviour.
        var unavailableLine = cart.Items.FirstOrDefault(i =>
            OfferResolution.ResolveOffer(offerCopies, checkoutTenantId, i.ProductId, i.VariantId) is not null
            && OfferResolution.ResolveOffer(offerCopies, checkoutTenantId, i.ProductId, i.VariantId, approvedSupplierIds) is null);
        if (unavailableLine is not null)
        {
            return TypedResults.BadRequest(
                $"{unavailableLine.Title} is currently unavailable and was removed; its supplier is not approved.");
        }

        // Re-validate prices against the current catalog copy (plan edge case: price drift → 409), then apply
        // the offer-as-price override: when an effective offer applies to a line's storefront, its price is the
        // AUTHORITATIVE charge (shown == charged), so it wins over the catalog price and does NOT trip the drift
        // 409 — the shopper saw that offer price on the storefront.
        var priceChanged = false;
        foreach (var item in cart.Items)
        {
            var original = item.UnitPriceMinor;
            var current = item.VariantId is { } variantId
                ? await db.ProductVariantCopies.Include(v => v.Prices).FirstOrDefaultAsync(v => v.VariantId == variantId, ct)
                : null;
            // Revalidate against the tenant's current price in the cart item's currency (per-currency pricing).
            var catalogPrice = current?.PriceInCurrency(item.Currency) ?? (await db.ProductCopies.FindAsync([item.ProductId], ct))?.MinPriceMinor;
            var offerPrice = OfferResolution.ResolvePricingOffer(
                offerCopies, checkoutTenantId, item.ProductId, item.VariantId, storefrontId, item.Currency, now, approvedSupplierIds)?.PriceMinor;

            // The offer price (when one is effective) is the authoritative charge; else fall back to the
            // current catalog price. Only a CATALOG drift (no offer overriding it) trips the review-your-cart
            // 409 — the offer price is what the shopper saw on the storefront, so charge it without a 409.
            if ((offerPrice ?? catalogPrice) is { } price)
            {
                item.UnitPriceMinor = price;
            }

            if (offerPrice is null && catalogPrice is { } cp && cp != original)
            {
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

        // Ship-to allowlist (cross-border guardrail): reject a destination the storefront doesn't serve
        // BEFORE authorizing any payment. Empty allowlist / no projected config = ships worldwide.
        // storefrontId is resolved up front (above) for attribution — reuse it here.
        var shipToCountry = request.ShippingAddress.Country.Trim().ToUpperInvariant();
        // Fetch the copy entity (not a .Select of the property) so EF applies the ShipToCountries value
        // converter on materialization — a projected converted collection comes back empty.
        var storefrontCopy = await db.StorefrontTaxCopies.AsNoTracking()
            .FirstOrDefaultAsync(t => t.StorefrontId == storefrontId, ct);
        if (storefrontCopy is { ShipToCountries.Count: > 0 } && !storefrontCopy.ShipToCountries.Contains(shipToCountry))
        {
            return TypedResults.BadRequest($"This storefront does not ship to {shipToCountry}.");
        }

        // Per-product, per-country ship rules (projected from Catalog via ProductUpserted, ADR-0008).
        // Load the copies as entities (not a .Select of the property) so EF applies the jsonb ShipRules
        // value converter on materialization — a projected converted collection comes back empty.
        // cartProductIds is computed up front (reused for the offer read model) — reuse it here.
        var ruleCopies = await db.ProductCopies.AsNoTracking()
            .Where(p => cartProductIds.Contains(p.ProductId))
            .ToListAsync(ct);
        ProductShipRule? RuleFor(Guid productId) =>
            ruleCopies.FirstOrDefault(c => c.ProductId == productId)?.RuleFor(shipToCountry);

        var subtotal = cart.Items.Sum(i => i.UnitPriceMinor * i.Quantity);
        // Goods subject to destination tax: a rule with ChargeDestinationTax=false exempts the line
        // (no resolved rule = taxed as normal). The full subtotal is still charged; only tax shrinks.
        var taxableSubtotal = cart.Items
            .Where(i => RuleFor(i.ProductId) is not { ChargeDestinationTax: false })
            .Sum(i => i.UnitPriceMinor * i.Quantity);
        // Shipping is waived only when EVERY line's resolved rule marks shipping already covered.
        var allShippingCovered = cart.Items.All(i => RuleFor(i.ProductId)?.ShippingCovered == true);

        // Storefront-wide discount (rev_disc): a per-storefront percentage deducted from the ITEMS' subtotal
        // only — never shipping, never tax — applied AFTER the per-line offer/catalog price resolved above
        // (it rides on whatever the shopper was charged per line). Basis points come from the same projected
        // StorefrontTaxCopy (by storefront id) fetched for the ship-to gate. Capped at the subtotal so the
        // discount can never exceed the goods value (nor turn the order negative), matching PricingEngine.
        var discountBps = storefrontCopy?.DiscountBasisPoints ?? 0;
        // It is combined with the promotion discount (ADR-0051) only AFTER shipping is final — see below.
        var storefrontDiscountMinor = discountBps > 0
            ? (long)Math.Round(subtotal * discountBps / 10000.0, MidpointRounding.AwayFromZero)
            : 0L;

        // Gate shipping from the OfferCopy read model (loaded up front): only a cart with at least one
        // shippable line is charged shipping — a non-shippable (e.g. digital/service/usage) order ships
        // nothing and must not pay shipping (mt4 / ADR-0028). Which product types ship is the tenant's
        // configurable ProductType policy (projected as ProductTypeShippingPolicyCopy); when no policy copy
        // or a line's product type is unknown, it falls back to the fulfilment-type gate.
        var shippingPolicy = await db.ProductTypeShippingPolicyCopies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == checkoutTenantId, ct);
        var anyShippable = cart.Items.Any(i => LineRequiresShipping(
            OfferResolution.ResolveOffer(offerCopies, checkoutTenantId, i.ProductId, i.VariantId, approvedSupplierIds), shippingPolicy));

        // "Collect at warehouse" (mt4 / ADR-0028): the shopper elects to collect the order from the fulfilling
        // supplier's warehouse instead of carrier delivery. Eligible only when the cart has at least one
        // physical Warehouse-fulfilment line from an approved supplier — that line's stock is collected, not
        // shipped. A collect order carries NO carrier and ZERO shipping and records the warehouse address it
        // is collected from (projected from Entity into SupplierWarehouseCopy). An ineligible collect request
        // is rejected so the client falls back to a shipped rate — normal shipped/dropship flows are untouched.
        SupplierWarehouseCopy? collectWarehouse = null;
        if (request.CollectAtWarehouse)
        {
            var warehouseOffer = cart.Items
                .Select(i => OfferResolution.ResolveOffer(offerCopies, checkoutTenantId, i.ProductId, i.VariantId, approvedSupplierIds))
                .FirstOrDefault(o => o is { FulfilmentType: FulfilmentType.Warehouse });
            if (warehouseOffer is null)
            {
                return TypedResults.BadRequest("Collect at warehouse is only available for warehouse-fulfilled items.");
            }

            collectWarehouse = await db.SupplierWarehouseCopies.AsNoTracking()
                .FirstOrDefaultAsync(w => w.SupplierId == warehouseOffer.SupplierId, ct);
        }

        var requestedShippingMinor = request.SelectedShippingAmountMinor ?? FlatShippingMinor;
        if (requestedShippingMinor < 0)
        {
            return TypedResults.BadRequest("Selected shipping amount cannot be negative.");
        }

        // Nothing shippable, or collect-at-warehouse → no carrier and no shipping charge, whatever the client sent.
        var shippingMinor = (anyShippable && !request.CollectAtWarehouse) ? requestedShippingMinor : 0L;

        // Carrier-rate validation applies to shipped orders only; a collect order ignores any selected rate.
        if (!request.CollectAtWarehouse)
        {
            if (request.SelectedShippingAmountMinor is not null &&
                (string.IsNullOrWhiteSpace(request.SelectedShippingService) || request.SelectedShippingExpiresAt is null))
            {
                return TypedResults.BadRequest("Selected shipping requires a service and expiry.");
            }

            if (request.SelectedShippingExpiresAt is { } expiresAt && expiresAt <= time.GetUtcNow())
            {
                return TypedResults.BadRequest("Selected shipping quote has expired; refresh shipping options.");
            }
        }

        // Waive shipping AFTER the selected-shipping guards so a covered order still validates its quote.
        if (allShippingCovered)
        {
            shippingMinor = 0;
        }

        // Threshold promotions (ADR-0051) are evaluated HERE, last on the shipping side, through the SAME
        // PromotionEvaluator that PricingEngine and GET /cart/summary use — the promotion algorithm exists
        // in exactly one place. shippingMinor is only final after the non-shippable / collect-at-warehouse /
        // quote-validation / allShippingCovered guards above, so a free-shipping promotion scores its
        // benefit against the rate the cart would ACTUALLY pay (0 when the cart already ships free) and can
        // never resurrect a rate a quote guard rejected — it only ever zeroes an already-computed charge.
        // The measurement base is each line's UnitPriceMinor, i.e. the offer-resolved effective selling
        // price settled above (ADR-0047/0048), excluding tax, shipping and fees.
        var promotionLines = cart.Items
            .Select(i => new PromotionLine(i.ProductId, i.UnitPriceMinor, i.Quantity))
            .ToList();
        var promotionOutcome = PromotionEvaluator.Evaluate(
            promotionLines, promotionCopies, checkoutTenantId, storefrontId, currency, shippingMinor, now);
        if (promotionOutcome.FreeShippingApplied)
        {
            shippingMinor = 0;
        }

        // The promotion discount and the storefront-wide discount (a store SETTING, never governed by the
        // combinable flag) stack additively and are jointly capped at the subtotal, so the goods can never
        // be discounted below zero. Same shape as PricingEngine.
        var discountMinor = Math.Clamp(promotionOutcome.DiscountMinor + storefrontDiscountMinor, 0, subtotal);

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
        // Tax base excludes ship-rule-exempt goods; the charge base always keeps the full subtotal so the
        // shopper still pays for exempt lines — only the tax portion shrinks (see ProductShipRule).
        // Shipping follows the goods' taxability: when no goods are destination-taxable (a fully exempt
        // cart, taxableSubtotal == 0) shipping is not destination-taxed either.
        var taxableShippingMinor = taxableSubtotal > 0 ? shippingMinor : 0;
        // The discount reduces the taxable base EXACTLY, per line (ADR-0051). A single proportional ratio
        // is right for a uniform storefront-wide percentage but wrong for a product-scoped promotion, whose
        // discount may fall entirely on a tax-exempt line (or entirely on a taxable one). So: the promotion
        // part comes from the evaluator's per-line vector, the storefront-wide part is a uniform percentage
        // spread proportionally by line value (largest remainder, so the parts sum to the whole), and only
        // the allocations landing on destination-taxable lines enter the base. Clamped at the taxable
        // subtotal so the base can never go negative.
        var lineTotalsMinor = cart.Items.Select(i => (long)i.UnitPriceMinor * i.Quantity).ToArray();
        var storefrontLineDiscounts = PromotionEvaluator.AllocateProportionally(storefrontDiscountMinor, lineTotalsMinor);
        long taxableDiscountMinor = 0;
        for (var i = 0; i < cart.Items.Count; i++)
        {
            if (RuleFor(cart.Items[i].ProductId) is { ChargeDestinationTax: false })
            {
                continue;
            }

            taxableDiscountMinor += promotionOutcome.LineDiscountsMinor[i] + storefrontLineDiscounts[i];
        }

        taxableDiscountMinor = Math.Min(taxableDiscountMinor, taxableSubtotal);
        var taxBaseMinor = taxableSubtotal - taxableDiscountMinor + taxableShippingMinor;
        var chargeBaseMinor = subtotal - discountMinor + shippingMinor;
        long taxMinor, netMinor;
        if (taxConfig?.TaxInclusive == true)
        {
            taxMinor = (long)Math.Round(taxBaseMinor * taxBps / (10000.0 + taxBps), MidpointRounding.AwayFromZero);
            netMinor = chargeBaseMinor; // listed price IS the charge
        }
        else
        {
            taxMinor = (long)Math.Round(taxBaseMinor * taxBps / 10000.0, MidpointRounding.AwayFromZero);
            netMinor = chargeBaseMinor + taxMinor;
        }

        if (priceChanged)
        {
            await db.SaveChangesAsync(ct);
            return TypedResults.Conflict(new CheckoutResponse(
                Guid.Empty, null, subtotal, discountMinor, shippingMinor, taxMinor, netMinor, currency, "Prices changed; review your cart.",
                promotionOutcome.FreeShippingApplied, promotionOutcome.AppliedPromotionIds));
        }

        var paymentOption = NormalizePaymentOption(request.PaymentOption);
        var paymentInstrumentSummary = PaymentInstrumentSummary(paymentOption, request.PaymentInstrumentSummary);

        var orderId = Guid.CreateVersion7();
        var idempotencyKey = orderId.ToString();
        // tenantId + storefrontId were resolved up front (they gate the offer price + shipping); the
        // storefront rides the AuthorizePayment request → Payment so the sale posts to the store's accounts.

        // A recurring (subscription/periodic) line can only be purchased by a verified, signed-in member
        // with a reusable payment instrument — never as a guest, and never with a one-off card, so renewals
        // can charge off-session (a stored card or a direct-debit mandate). Non-recurring carts are
        // unaffected. email_verified rides the gateway-minted internal claims (InternalClaimsMinter).
        var hasRecurringLine = cart.Items.Any(i =>
            OfferResolution.ResolveOffer(offerCopies, checkoutTenantId, i.ProductId, i.VariantId, approvedSupplierIds)?.BillingMode == BillingMode.Recurring);
        if (hasRecurringLine)
        {
            if (userId is null)
            {
                return TypedResults.BadRequest("Sign in as a member to purchase a subscription.");
            }

            if (!string.Equals(http.User.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return TypedResults.BadRequest("Verify your email address to purchase a subscription.");
            }

            if (request.SavedPaymentMethodId is null)
            {
                return TypedResults.BadRequest("A saved payment method or direct-debit mandate is required for a subscription.");
            }
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
            // Promotion snapshot (ADR-0051): why this charge was what it was. The ids are comma-joined
            // (null when none applied) so a past order can be explained without re-running the evaluator.
            PromotionDiscountMinor = promotionOutcome.DiscountMinor,
            AppliedPromotionIds = promotionOutcome.AppliedPromotionIds.Count == 0
                ? null
                : string.Join(',', promotionOutcome.AppliedPromotionIds),
            FreeShippingApplied = promotionOutcome.FreeShippingApplied,
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
            ShipRegion = string.IsNullOrWhiteSpace(request.ShippingAddress.Region) ? null : request.ShippingAddress.Region.Trim(),
            ShipPostcode = request.ShippingAddress.Postcode,
            ShipCountry = request.ShippingAddress.Country,
            CollectAtWarehouse = request.CollectAtWarehouse,
            WarehouseName = collectWarehouse?.Name,
            WarehouseLine1 = collectWarehouse?.Line1,
            WarehouseCity = collectWarehouse?.City,
            WarehousePostcode = collectWarehouse?.Postcode,
            WarehouseCountry = collectWarehouse?.CountryCode,
            CreatedAt = now,
            Lines = cart.Items.Select((i, index) =>
            {
                var offer = OfferResolution.ResolveOffer(offerCopies, tenantId, i.ProductId, i.VariantId, approvedSupplierIds);
                return new CheckoutAttemptLine
                {
                    Id = Guid.CreateVersion7(),
                    CheckoutAttemptId = orderId,
                    ProductId = i.ProductId,
                    VariantId = i.VariantId,
                    VariantSku = i.VariantSku,
                    Title = i.Title,
                    UnitPriceMinor = i.UnitPriceMinor,
                    // The promotion allocation for THIS line (largest remainder; the vector sums to
                    // PromotionDiscountMinor exactly). The storefront-wide discount stays at the order level.
                    DiscountMinor = promotionOutcome.LineDiscountsMinor[index],
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
            orderId, intent.ClientSecret, subtotal, discountMinor, shippingMinor, taxMinor, intent.GrossMinor, currency, null,
            promotionOutcome.FreeShippingApplied, promotionOutcome.AppliedPromotionIds));
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
    [property: Required, StringLength(2, MinimumLength = 2)] string Country,
    // Sub-national region (state/province/county/…); optional — not every country's address has one.
    [property: StringLength(120)] string? Region = null);

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
    // "Collect at warehouse" (mt4 / ADR-0028): collect from the fulfilling supplier's warehouse instead of
    // carrier delivery — zero shipping, no carrier. Requires an eligible Warehouse-fulfilment line.
    bool CollectAtWarehouse = false,
    // The active storefront (phase 2). The gateway derives the trusted X-3C-Storefront-Id from the host,
    // which can't distinguish path-based demo storefronts, so the first-party storefront app also sends
    // it here for per-storefront order/ledger attribution. Header wins when present.
    Guid? StorefrontId = null);

// Positional record: NEW FIELDS MUST BE APPENDED WITH DEFAULTS, or every call site and the storefront
// client break. FreeShippingApplied / AppliedPromotionIds are the threshold-promotion outcome (ADR-0051).
public record CheckoutResponse(
    Guid OrderId, string? ClientSecret, long NetMinor, long DiscountMinor, long ShippingMinor, long TaxMinor,
    long GrossMinor, string Currency, string? Message,
    bool FreeShippingApplied = false, IReadOnlyList<Guid>? AppliedPromotionIds = null);
