using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Catalog's PromotionChanged → Ordering PromotionCopy projection (ADR-0051 / ADR-0008): the read copy
/// checkout and GET /cart/summary evaluate promotions from, without a cross-service query. Proves the
/// consumer is registered (an unregistered consumer silently never runs), that the upsert is idempotent
/// (a re-published promotion updates its row rather than duplicating it), and that a deactivation
/// projects so the promotion stops applying.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class PromotionProjectionTests(Phase3Fixture fixture)
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task PromotionChanged_creates_the_copy_with_every_field()
    {
        var promotionId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var until = DateTimeOffset.UtcNow.AddDays(30);

        await PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, "Spend 100 ship free", "NZD",
            PromotionScopeKind.Storefront, ProductId: null,
            MinimumAmountMinor: 10_000, MinimumQuantity: 0,
            GrantsFreeShipping: true, PercentOff: 0, DiscountAmountMinor: 0,
            Combinable: false, Active: true, ActiveFrom: from, ActiveUntil: until));

        var copy = await WaitForCopyAsync(promotionId, c => c.MinimumAmountMinor == 10_000);
        Assert.Equal(TenantId, copy.TenantId);
        Assert.Equal(storefrontId, copy.StorefrontId);
        Assert.Equal("Spend 100 ship free", copy.Name);
        Assert.Equal("NZD", copy.Currency);
        Assert.Equal(PromotionScopeKind.Storefront, copy.Scope);
        Assert.Null(copy.ProductId);
        Assert.Equal(0, copy.MinimumQuantity);
        Assert.True(copy.GrantsFreeShipping);
        Assert.False(copy.Combinable);
        Assert.True(copy.Active);
        Assert.NotNull(copy.ActiveFrom);
        Assert.NotNull(copy.ActiveUntil);

        // The copy is effective for its own storefront + currency and never for another currency (no FX).
        Assert.True(copy.IsEffectiveFor(TenantId, storefrontId, "NZD", DateTimeOffset.UtcNow));
        Assert.False(copy.IsEffectiveFor(TenantId, storefrontId, "AUD", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task PromotionChanged_is_an_idempotent_upsert_not_a_duplicate_insert()
    {
        var promotionId = Guid.CreateVersion7();
        var productId = Guid.CreateVersion7();

        await PublishAsync(new PromotionChanged(
            promotionId, TenantId, StorefrontId: null, "Buy 3 get 10%", "NZD",
            PromotionScopeKind.Product, productId,
            MinimumAmountMinor: 0, MinimumQuantity: 3,
            GrantsFreeShipping: false, PercentOff: 10, DiscountAmountMinor: 0,
            Combinable: true, Active: true));
        await WaitForCopyAsync(promotionId, c => c.PercentOff == 10);

        // Re-publish the SAME promotion id with a different reward: the one row must be updated.
        await PublishAsync(new PromotionChanged(
            promotionId, TenantId, StorefrontId: null, "Buy 3 get 20%", "NZD",
            PromotionScopeKind.Product, productId,
            MinimumAmountMinor: 0, MinimumQuantity: 3,
            GrantsFreeShipping: false, PercentOff: 20, DiscountAmountMinor: 0,
            Combinable: true, Active: true));
        var updated = await WaitForCopyAsync(promotionId, c => c.PercentOff == 20);
        Assert.Equal("Buy 3 get 20%", updated.Name);
        Assert.Equal(PromotionScopeKind.Product, updated.Scope);
        Assert.Equal(productId, updated.ProductId);

        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        Assert.Equal(1, db.PromotionCopies.Count(p => p.PromotionId == promotionId));
    }

    [Fact]
    public async Task Deactivating_a_promotion_projects_and_makes_the_copy_ineffective()
    {
        var promotionId = Guid.CreateVersion7();
        var storefrontId = Guid.CreateVersion7();

        await PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, "Spend 50 take 5 off", "NZD",
            PromotionScopeKind.Storefront, ProductId: null,
            MinimumAmountMinor: 5_000, MinimumQuantity: 0,
            GrantsFreeShipping: false, PercentOff: 0, DiscountAmountMinor: 500,
            Combinable: true, Active: true));
        await WaitForCopyAsync(promotionId, c => c.Active);

        await PublishAsync(new PromotionChanged(
            promotionId, TenantId, storefrontId, "Spend 50 take 5 off", "NZD",
            PromotionScopeKind.Storefront, ProductId: null,
            MinimumAmountMinor: 5_000, MinimumQuantity: 0,
            GrantsFreeShipping: false, PercentOff: 0, DiscountAmountMinor: 500,
            Combinable: true, Active: false));
        var deactivated = await WaitForCopyAsync(promotionId, c => !c.Active);
        Assert.False(deactivated.IsEffectiveFor(TenantId, storefrontId, "NZD", DateTimeOffset.UtcNow));
    }

    private async Task PublishAsync(PromotionChanged message)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<MassTransit.IPublishEndpoint>();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        await bus.Publish(message);
        await db.SaveChangesAsync(); // flush the transactional outbox
    }

    private async Task<PromotionCopy> WaitForCopyAsync(Guid promotionId, Func<PromotionCopy, bool> matches)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var copy = await db.PromotionCopies.FindAsync(promotionId);
            if (copy is not null && matches(copy))
            {
                return copy;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"PromotionCopy {promotionId} did not reach the expected state.");
    }
}
