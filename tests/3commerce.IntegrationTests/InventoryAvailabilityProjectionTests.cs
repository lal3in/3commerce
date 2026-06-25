using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Catalog.Infrastructure.Consumers;

namespace ThreeCommerce.IntegrationTests;

/// <summary>mt4_2: Catalog mirrors Fulfillment-owned availability onto its variant read model (ADR-0028).</summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class InventoryAvailabilityProjectionTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;

    public Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _catalog.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Availability_event_overwrites_the_variant_stock_read_model()
    {
        var tenant = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var variantId = Guid.CreateVersion7();
        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var categoryId = Guid.CreateVersion7();
        db.Categories.Add(new Category { Id = categoryId, TenantId = tenant, Slug = $"cat-{categoryId:N}", Name = "Cat" });
        db.Products.Add(new Product
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            Slug = $"p-{variantId:N}",
            Title = "P",
            Brand = "B",
            CategoryId = categoryId,
            Variants = [new Variant { Id = variantId, Sku = $"sku-{variantId:N}", PriceMinor = 1000, Currency = "EUR", StockQuantity = 0 }],
        });
        await db.SaveChangesAsync();

        await InventoryAvailabilityConsumer.ApplyAsync(db, variantId, 42, default);

        var reloaded = await db.Variants.FindAsync(variantId);
        Assert.Equal(42, reloaded!.StockQuantity);

        // Idempotent absolute overwrite (not additive).
        await InventoryAvailabilityConsumer.ApplyAsync(db, variantId, 7, default);
        await db.Entry(reloaded).ReloadAsync();
        Assert.Equal(7, reloaded.StockQuantity);
    }

    [Fact]
    public async Task Product_level_availability_is_ignored()
    {
        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        // No variant id → no-op, must not throw.
        await InventoryAvailabilityConsumer.ApplyAsync(db, null, 99, default);
    }
}
