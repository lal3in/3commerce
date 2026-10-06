using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Entity;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Shown == sellable == charged (ADR-0059): Ordering's checkout availability gate is CATALOG'S gate
/// (ADR-0048) — evaluated per storefront AND currency. Each case builds the offers once, through Catalog's real
/// admin endpoints (OfferChanged reaches Ordering's OfferCopy through the outbox), and asks BOTH sides the
/// same question: does the storefront listing show the product, and does checkout on that storefront sell it?
/// Before ADR-0059 checkout accepted a line when ANY active offer from an approved supplier existed in any
/// currency or store, so a product hidden from the listing could still be bought through the API — and an
/// unapproved offer elsewhere refused a product the listing showed.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class CheckoutOfferGateParityTests(Phase3Fixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private HttpClient _admin = null!;
    private HttpClient _public = null!;
    private Guid _categoryId;
    private string _categorySlug = "";

    private sealed record CurrencyPriceDto(string Currency, long PriceMinor);
    private sealed record VariantDto(Guid? Id, string Sku, long PriceMinor, string? Currency, int StockQuantity, List<CurrencyPriceDto>? Prices = null,
        int? WeightGrams = 500, int? LengthMm = 200, int? WidthMm = 150, int? HeightMm = 100,
        int? PackageWeightGrams = 650, int? PackageLengthMm = 250, int? PackageWidthMm = 200, int? PackageHeightMm = 150);
    private sealed record EditorDto(Guid Id, string Slug, string Title);
    private sealed record StorefrontDto(Guid Id, string Name);
    private sealed record OfferDto(Guid Id);
    private sealed record HitDto(Guid Id, string Slug, long MinPriceMinor, string Currency);
    private sealed record DetailVariantDto(Guid Id, string Sku, long PriceMinor, string Currency, bool InStock);
    private sealed record DetailDto(Guid Id, string Slug, List<DetailVariantDto> Variants);
    private sealed record CheckoutResponseDto(Guid OrderId, long NetMinor, long GrossMinor, string Currency, string? Message);
    private sealed record CartSummaryDto(long SubtotalMinor, string Currency, int CheckoutBlock = 0, string? CheckoutBlockedReason = null);

    private sealed record Product(Guid Id, Guid VariantId, string Slug);

    public async Task InitializeAsync()
    {
        // Touch the lazy Catalog host FIRST so its consumer queues exist before any approval is published.
        _admin = fixture.Catalog.CreateClient();
        _admin.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin));
        _public = fixture.Catalog.CreateClient();

        using var scope = fixture.Catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        _categoryId = Guid.CreateVersion7();
        _categorySlug = $"parity-{_categoryId:N}";
        db.Categories.Add(new Category { Id = _categoryId, TenantId = TenantId, Slug = _categorySlug, Name = "Parity" });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _public.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task An_offer_approved_only_for_another_storefront_is_hidden_here_and_refused_here()
    {
        var here = await StorefrontAsync("EUR");
        var elsewhere = await StorefrontAsync("EUR");
        var product = await ProductAsync(2_000, "EUR", here, elsewhere);
        var approved = await SupplierAsync(approved: true);
        var unapproved = await SupplierAsync(approved: false);

        await OfferAsync(product, approved, "EUR", storefrontId: elsewhere); // approved — but only for the other store
        await OfferAsync(product, unapproved, "EUR", storefrontId: here);    // the only supply here: unapproved

        // HERE: the listing hides it, the detail marks it unavailable, and checkout refuses it — agreeing.
        Assert.False(await ListedAsync(here, "EUR", product));
        Assert.False(await InStockAsync(here, "EUR", product));
        await AssertRefusedAsync(here, product);

        // ELSEWHERE: the approved offer covers it — listed and sold.
        Assert.True(await ListedAsync(elsewhere, "EUR", product));
        await AssertSoldAsync(elsewhere, product);
    }

    [Fact]
    public async Task An_offer_approved_only_in_another_currency_is_hidden_and_refused_in_this_one()
    {
        var store = await StorefrontAsync("EUR");
        var product = await ProductAsync(2_000, "EUR", store);
        var approved = await SupplierAsync(approved: true);
        var unapproved = await SupplierAsync(approved: false);

        await OfferAsync(product, approved, "USD");   // approved, but in another currency
        await OfferAsync(product, unapproved, "EUR"); // the only EUR supply: unapproved

        Assert.False(await ListedAsync(store, "EUR", product));
        Assert.False(await InStockAsync(store, "EUR", product));
        await AssertRefusedAsync(store, product);
    }

    [Fact]
    public async Task An_unapproved_offer_for_another_storefront_or_currency_no_longer_blocks_a_listed_product()
    {
        // The other direction of the same defect: nothing covers this store in EUR, so Catalog lists the product
        // at its catalogue price — and checkout used to refuse it because of supply that is not even sold here.
        var here = await StorefrontAsync("EUR");
        var elsewhere = await StorefrontAsync("EUR");
        var product = await ProductAsync(2_000, "EUR", here);
        var unapproved = await SupplierAsync(approved: false);

        await OfferAsync(product, unapproved, "EUR", storefrontId: elsewhere);
        await OfferAsync(product, unapproved, "USD");

        Assert.True(await ListedAsync(here, "EUR", product));
        Assert.True(await InStockAsync(here, "EUR", product));
        var order = await AssertSoldAsync(here, product);
        Assert.Equal(2_000, order.NetMinor); // the catalogue price governs an offerless line
    }

    [Fact]
    public async Task An_offer_approved_for_this_store_and_currency_is_listed_and_sold_at_the_offer_price()
    {
        var store = await StorefrontAsync("EUR");
        var product = await ProductAsync(2_000, "EUR", store);
        var approved = await SupplierAsync(approved: true);

        await OfferAsync(product, approved, "EUR", storefrontId: store, priceMinor: 1_500);

        Assert.True(await ListedAsync(store, "EUR", product));
        Assert.True(await InStockAsync(store, "EUR", product));
        var order = await AssertSoldAsync(store, product);
        Assert.Equal(1_500, order.NetMinor); // shown == charged: the offer price
    }

    [Fact]
    public async Task An_offerless_product_is_listed_and_sold_at_its_catalogue_price()
    {
        var store = await StorefrontAsync("EUR");
        var product = await ProductAsync(2_000, "EUR", store);

        Assert.True(await ListedAsync(store, "EUR", product));
        var order = await AssertSoldAsync(store, product);
        Assert.Equal(2_000, order.NetMinor);
    }

    // ---- The two sides ---------------------------------------------------------------------------------

    private async Task<bool> ListedAsync(Guid storefrontId, string currency, Product product)
    {
        var hits = await _public.GetFromJsonAsync<List<HitDto>>(
            $"/products?currency={currency}&storefrontId={storefrontId}&category={_categorySlug}&pageSize=100");
        return hits!.Any(h => h.Id == product.Id);
    }

    private async Task<bool> InStockAsync(Guid storefrontId, string currency, Product product)
    {
        var detail = await _public.GetFromJsonAsync<DetailDto>($"/products/{product.Slug}?currency={currency}&storefrontId={storefrontId}");
        return detail!.Variants.Single(v => v.Id == product.VariantId).InStock;
    }

    private async Task AssertRefusedAsync(Guid storefrontId, Product product)
    {
        using var shopper = await ShopperWithCartAsync(storefrontId, product);

        // The cart preview flags it in checkout's own words before the shopper pays.
        var summary = (await shopper.GetFromJsonAsync<CartSummaryDto>($"/cart/summary?storefrontId={storefrontId}"))!;
        Assert.Equal(2, summary.CheckoutBlock); // CheckoutBlock.SupplyUnavailable, as a number

        var response = await shopper.PostAsJsonAsync("/checkout", CheckoutBody(storefrontId));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("supplier is not approved", body, StringComparison.Ordinal);
        Assert.Contains(summary.CheckoutBlockedReason!, body, StringComparison.Ordinal);
    }

    private async Task<CheckoutResponseDto> AssertSoldAsync(Guid storefrontId, Product product)
    {
        using var shopper = await ShopperWithCartAsync(storefrontId, product);
        var summary = (await shopper.GetFromJsonAsync<CartSummaryDto>($"/cart/summary?storefrontId={storefrontId}"))!;
        Assert.Equal(0, summary.CheckoutBlock);

        var response = await shopper.PostAsJsonAsync("/checkout", CheckoutBody(storefrontId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
    }

    private async Task<HttpClient> ShopperWithCartAsync(Guid storefrontId, Product product)
    {
        var shopper = fixture.Ordering.CreateClient();
        shopper.DefaultRequestHeaders.Add("X-3C-Storefront-Id", storefrontId.ToString());
        (await shopper.PostAsJsonAsync("/cart/items", new { productId = product.Id, variantId = product.VariantId, quantity = 1, currency = "EUR" }))
            .EnsureSuccessStatusCode();
        return shopper;
    }

    private static object CheckoutBody(Guid storefrontId) => new
    {
        email = "parity@example.com",
        storefrontId,
        shippingAddress = new { name = "P", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
    };

    // ---- Setup through the real services --------------------------------------------------------------

    /// <summary>
    /// A Catalog storefront (publications + the storefront-scoped listing need the row), made LIVE in
    /// Ordering's projection. Catalog creates it as Draft and projects IsLive=false; waiting for that copy
    /// first, then publishing the live config, keeps the Draft event from overwriting it later.
    /// </summary>
    private async Task<Guid> StorefrontAsync(string currency)
    {
        var response = await _admin.PostAsJsonAsync("/admin/storefronts", new
        {
            tenantId = TenantId,
            name = $"Parity-{Guid.NewGuid():N}"[..20],
            visibility = 1,
            currency,
        });
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<StorefrontDto>())!.Id;

        await WaitAsync(async db => await db.StorefrontTaxCopies.AsNoTracking().AnyAsync(c => c.StorefrontId == id), $"Storefront {id} (draft)");
        await fixture.PublishAsync(new StorefrontConfigChanged(id, TenantId, "Parity", currency, 0, IsLive: true, TaxInclusive: false, DiscountBps: 0));
        await WaitAsync(async db => await db.StorefrontTaxCopies.AsNoTracking().AnyAsync(c => c.StorefrontId == id && c.IsLive), $"Storefront {id} (live)");
        return id;
    }

    /// <summary>A physical product priced in <paramref name="currency"/>, published on each given storefront and
    /// projected into Ordering (ProductUpserted) so the cart can hold it.</summary>
    private async Task<Product> ProductAsync(long priceMinor, string currency, params Guid[] storefronts)
    {
        var slug = $"parity-p-{Guid.NewGuid():N}";
        var create = await _admin.PostAsJsonAsync("/admin/products", new
        {
            slug,
            title = "Parity Product",
            brand = "Acme",
            description = "x",
            categoryId = _categoryId,
            attributes = new Dictionary<string, string>(),
            imageUrls = new[] { "https://example.test/img.png" },
            variants = new[] { new VariantDto(null, $"PAR-{Guid.NewGuid():N}"[..12], priceMinor, currency, 5) },
        });
        create.EnsureSuccessStatusCode();
        var productId = (await create.Content.ReadFromJsonAsync<EditorDto>())!.Id;

        foreach (var storefrontId in storefronts)
        {
            (await _admin.PostAsJsonAsync($"/admin/storefronts/{storefrontId}/products",
                new { productId, fulfillmentSource = 2, countryOfOrigin = "AU" })).EnsureSuccessStatusCode();
            (await _admin.PostAsync($"/admin/storefronts/{storefrontId}/products/{productId}/publish", null)).EnsureSuccessStatusCode();
        }

        Guid variantId;
        using (var scope = fixture.Catalog.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            variantId = await catalog.Variants.AsNoTracking().Where(v => v.ProductId == productId).Select(v => v.Id).SingleAsync();
        }

        await WaitAsync(async db => await db.ProductCopies.AsNoTracking().Include(p => p.Variants)
            .AnyAsync(p => p.ProductId == productId && p.Variants.Any(v => v.VariantId == variantId)), $"Product {productId}");
        return new Product(productId, variantId, slug);
    }

    /// <summary>A supplier whose approval state reaches BOTH projections (Entity's SupplierApprovalChanged).</summary>
    private async Task<Guid> SupplierAsync(bool approved)
    {
        var supplierId = Guid.CreateVersion7();
        await fixture.PublishAsync(new SupplierApprovalChanged(TenantId, supplierId, approved));
        await WaitAsync(async db => await db.SupplierApprovalCopies.AsNoTracking()
            .AnyAsync(s => s.SupplierId == supplierId && s.Approved == approved), $"Ordering approval {supplierId}");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Catalog.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            if (await catalog.SupplierApprovalCopies.AsNoTracking().AnyAsync(s => s.SupplierId == supplierId && s.Approved == approved))
            {
                return supplierId;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Catalog approval {supplierId} did not project.");
    }

    /// <summary>An active, product-level offer created through Catalog's admin API, then awaited in Ordering.</summary>
    private async Task OfferAsync(Product product, Guid supplierId, string currency, Guid? storefrontId = null, long priceMinor = 0)
    {
        var response = await _admin.PostAsJsonAsync("/admin/offers", new
        {
            tenantId = TenantId,
            productId = product.Id,
            variantId = (Guid?)null,
            supplierId,
            supplyCategory = SupplyCategory.Physical,
            fulfilmentType = FulfilmentType.Dropship,
            priceMinor,
            currency,
            priority = 0,
            storefrontId,
        });
        response.EnsureSuccessStatusCode();
        var offerId = (await response.Content.ReadFromJsonAsync<OfferDto>())!.Id;
        await WaitAsync(async db => await db.OfferCopies.AsNoTracking().AnyAsync(o => o.OfferId == offerId), $"OfferCopy {offerId}");
    }

    private async Task WaitAsync(Func<OrderingDbContext, Task<bool>> settled, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            if (await settled(scope.ServiceProvider.GetRequiredService<OrderingDbContext>()))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"{what} did not project into Ordering.");
    }
}
