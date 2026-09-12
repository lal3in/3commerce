using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Two write-time guards on <c>/admin/promotions</c> that the pricing audit found missing, both of the
/// same kind: an operator could save a promotion the system would then quietly refuse to honour.
/// <list type="bullet">
/// <item><b>Currency.</b> A promotion is currency-pinned and there is no FX anywhere, so one aimed at a
/// storefront that sells in a different currency can never apply to a single cart. It used to save
/// happily and simply never fire.</item>
/// <item><b>Code/threshold ordering.</b> Removing a coupon code while supplying a threshold in the SAME
/// request was rejected, because the code was cleared first and the guard saw the promotion as it was
/// rather than as the request would leave it — a legitimate edit refused on an ordering accident.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class PromotionAdminGuardTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _admin = null!;

    public Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        _admin = _catalog.CreateClient();
        _admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _catalog.Dispose();
        return Task.CompletedTask;
    }

    private sealed record PromotionDto(Guid Id, string Name, string Currency, string? Code);

    [Fact]
    public async Task A_promotion_aimed_at_a_storefront_of_another_currency_is_refused()
    {
        var storefrontId = await SeedStorefrontAsync("AUD");

        var response = await _admin.PostAsJsonAsync("/admin/promotions", new
        {
            tenantId = TenantId,
            name = $"Mismatched {Guid.CreateVersion7():N}",
            currency = "EUR",
            scope = 1,
            storefrontId,
            minimumAmountMinor = 5_000,
            percentOff = 10,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("EUR", body);
        Assert.Contains("AUD", body);
    }

    [Fact]
    public async Task A_promotion_in_its_storefronts_own_currency_is_accepted()
    {
        // The control: the guard must refuse the mismatch and nothing else.
        var storefrontId = await SeedStorefrontAsync("AUD");

        var response = await _admin.PostAsJsonAsync("/admin/promotions", new
        {
            tenantId = TenantId,
            name = $"Matched {Guid.CreateVersion7():N}",
            currency = "AUD",
            scope = 1,
            storefrontId,
            minimumAmountMinor = 5_000,
            percentOff = 10,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task An_all_storefront_promotion_needs_no_currency_match()
    {
        // With no storefront there is nothing to mismatch: it applies to every store of its own currency.
        var response = await _admin.PostAsJsonAsync("/admin/promotions", new
        {
            tenantId = TenantId,
            name = $"All stores {Guid.CreateVersion7():N}",
            currency = "EUR",
            scope = 1,
            minimumAmountMinor = 5_000,
            percentOff = 10,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_coupon_can_be_turned_into_an_automatic_promotion_in_one_request()
    {
        // A coupon with NO threshold — the commonest kind ("10% off with WELCOME10", no minimum spend).
        // Turning it into an automatic promotion requires supplying a threshold, and doing both at once is
        // the only sensible way to ask for it: the promotion is never, at any point, an automatic one with
        // no threshold. This used to 400, because the code was cleared before the threshold was applied.
        var created = await _admin.PostAsJsonAsync("/admin/promotions", new
        {
            tenantId = TenantId,
            name = $"Coupon to automatic {Guid.CreateVersion7():N}",
            currency = "EUR",
            scope = 1,
            percentOff = 10,
            code = $"SWAP{Guid.CreateVersion7():N}"[..12],
        });
        created.EnsureSuccessStatusCode();
        var promotion = (await created.Content.ReadFromJsonAsync<PromotionDto>())!;

        var response = await _admin.PutAsJsonAsync($"/admin/promotions/{promotion.Id}", new
        {
            applyCode = true,
            code = (string?)null,     // drop the coupon code …
            minimumAmountMinor = 5_000, // … and give it the threshold that makes that legal
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<PromotionDto>())!;
        Assert.Null(updated.Code);
    }

    [Fact]
    public async Task Dropping_a_coupon_code_with_no_threshold_is_still_refused()
    {
        // The guard itself must survive: without a threshold in the same request, clearing the code would
        // leave an automatic promotion that discounts every cart unconditionally.
        var created = await _admin.PostAsJsonAsync("/admin/promotions", new
        {
            tenantId = TenantId,
            name = $"Coupon stays {Guid.CreateVersion7():N}",
            currency = "EUR",
            scope = 1,
            percentOff = 10,
            code = $"KEEP{Guid.CreateVersion7():N}"[..12],
        });
        created.EnsureSuccessStatusCode();
        var promotion = (await created.Content.ReadFromJsonAsync<PromotionDto>())!;

        var response = await _admin.PutAsJsonAsync($"/admin/promotions/{promotion.Id}", new
        {
            applyCode = true,
            code = (string?)null,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<Guid> SeedStorefrontAsync(string currency)
    {
        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var now = DateTimeOffset.UtcNow;
        var storefront = Storefront.Create(TenantId, $"Guard Store {Guid.CreateVersion7():N}"[..24], now);
        storefront.ConfigureCommerce(string.Empty, currency, StorefrontTaxRegime.UsSalesTax, 0, now);
        db.Storefronts.Add(storefront);
        await db.SaveChangesAsync();
        return storefront.Id;
    }
}
