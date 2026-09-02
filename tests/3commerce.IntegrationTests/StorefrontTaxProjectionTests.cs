using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Catalog's StorefrontConfigChanged → Ordering StorefrontTaxCopy projection (ADR-0008 read copy
/// that checkout resolves the charged tax rate from) — review remediation rev_4 / finding F3.
/// Uses a unique NZD storefront so the shared-fixture EUR/AUD assertions elsewhere are untouched.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class StorefrontTaxProjectionTests(Phase3Fixture fixture)
{
    [Fact]
    public async Task StorefrontConfigChanged_upserts_the_tax_copy()
    {
        var storefrontId = Guid.CreateVersion7();
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        await PublishAsync(new StorefrontConfigChanged(storefrontId, tenantId, "NZ Store", "NZD", 1_500, IsLive: true, TaxInclusive: true));
        var created = await WaitForCopyAsync(storefrontId, c => c.TaxRateBasisPoints == 1_500 && c.IsLive && c.TaxInclusive);
        Assert.Equal("NZD", created.Currency);

        // Update: rate change + taken offline → same row updated, not duplicated.
        await PublishAsync(new StorefrontConfigChanged(storefrontId, tenantId, "NZ Store", "NZD", 1_250, IsLive: false, TaxInclusive: false));
        var updated = await WaitForCopyAsync(storefrontId, c => c.TaxRateBasisPoints == 1_250 && !c.IsLive && !c.TaxInclusive);
        Assert.Equal("NZD", updated.Currency);
    }

    [Fact]
    public async Task StorefrontConfigChanged_projects_the_storefront_discount()
    {
        var storefrontId = Guid.CreateVersion7();
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        // The storefront-wide discount (bps) must project onto the read copy so checkout can charge it.
        await PublishAsync(new StorefrontConfigChanged(
            storefrontId, tenantId, "Discount Store", "NZD", 0, IsLive: true, DiscountBps: 1_500));
        var withDiscount = await WaitForCopyAsync(storefrontId, c => c.DiscountBasisPoints == 1_500);
        Assert.Equal(1_500, withDiscount.DiscountBasisPoints);

        // Lowering it to 0 (discount removed) also projects — the copy is not left stale with the old rate.
        await PublishAsync(new StorefrontConfigChanged(
            storefrontId, tenantId, "Discount Store", "NZD", 0, IsLive: true, DiscountBps: 0));
        var cleared = await WaitForCopyAsync(storefrontId, c => c.DiscountBasisPoints == 0);
        Assert.Equal(0, cleared.DiscountBasisPoints);
    }

    [Fact]
    public async Task StorefrontConfigChanged_projects_the_ship_to_allowlist()
    {
        var storefrontId = Guid.CreateVersion7();
        var tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        // Regression guard: the allowlist must survive serialization end-to-end. A concrete string[]
        // (not IReadOnlyList) is what MassTransit's serializer materializes on the consumer.
        await PublishAsync(new StorefrontConfigChanged(
            storefrontId, tenantId, "Ship Store", "NZD", 0, IsLive: true, ShipToCountries: ["AU", "NZ"]));
        var withList = await WaitForCopyAsync(storefrontId, c => c.ShipToCountries.Count == 2);
        Assert.Equal(["AU", "NZ"], withList.ShipToCountries.OrderBy(c => c, StringComparer.Ordinal));

        // Clearing the list (worldwide) also projects — the copy is not left stale with the old codes.
        await PublishAsync(new StorefrontConfigChanged(
            storefrontId, tenantId, "Ship Store", "NZD", 0, IsLive: true, ShipToCountries: []));
        var cleared = await WaitForCopyAsync(storefrontId, c => c.ShipToCountries.Count == 0);
        Assert.Empty(cleared.ShipToCountries);
    }

    [Fact]
    public async Task ProductUpserted_projects_the_ship_rules_onto_the_product_copy()
    {
        var productId = Guid.CreateVersion7();

        // A specific-country rule plus the whole-world default must survive serialization end-to-end
        // (MassTransit materializes a concrete List on the consumer via the jsonb converter).
        await PublishProductAsync(new ProductUpserted(
            productId, $"p-{productId:N}", "Rule Product", 1_000, "EUR", null,
            [new ProductVariantUpserted(Guid.CreateVersion7(), "SKU-1", 1_000, "EUR", 5)],
            ShipRules: [new ProductShipRuleContract("DE", false, true), new ProductShipRuleContract("*", true, false)]));
        var withRules = await WaitForProductCopyAsync(productId, c => c.ShipRules.Count == 2);
        var de = withRules.ShipRules.Single(r => r.CountryCode == "DE");
        Assert.False(de.ChargeDestinationTax);
        Assert.True(de.ShippingCovered);

        // Clearing the rules also projects — the copy is not left stale with the old rules.
        await PublishProductAsync(new ProductUpserted(
            productId, $"p-{productId:N}", "Rule Product", 1_000, "EUR", null,
            [new ProductVariantUpserted(Guid.CreateVersion7(), "SKU-1", 1_000, "EUR", 5)],
            ShipRules: []));
        var cleared = await WaitForProductCopyAsync(productId, c => c.ShipRules.Count == 0);
        Assert.Empty(cleared.ShipRules);
    }

    [Fact]
    public async Task ProductUpserted_with_null_ship_rules_preserves_the_existing_copy_rules()
    {
        var productId = Guid.CreateVersion7();

        // Seed the copy WITH an admin-set rule.
        await PublishProductAsync(new ProductUpserted(
            productId, $"p-{productId:N}", "Rule Product", 1_000, "EUR", null,
            [new ProductVariantUpserted(Guid.CreateVersion7(), "SKU-1", 1_000, "EUR", 5)],
            ShipRules: [new ProductShipRuleContract("DE", false, true)]));
        await WaitForProductCopyAsync(productId, c => c.ShipRules.Count == 1);

        // A re-upsert that OMITS ShipRules (null — e.g. the CSV importer) must NOT wipe the rules; the
        // contract's null = "no per-country overrides carried", i.e. leave as-is (regression guard: it
        // used to clear to []). We wait on the rename to know the second message was projected, then
        // assert the rule is still present.
        await PublishProductAsync(new ProductUpserted(
            productId, $"p-{productId:N}", "Rule Product Renamed", 1_000, "EUR", null,
            [new ProductVariantUpserted(Guid.CreateVersion7(), "SKU-1", 1_000, "EUR", 5)],
            ShipRules: null));
        var after = await WaitForProductCopyAsync(productId, c => c.Title == "Rule Product Renamed");
        var rule = Assert.Single(after.ShipRules);
        Assert.Equal("DE", rule.CountryCode);
        Assert.False(rule.ChargeDestinationTax);
        Assert.True(rule.ShippingCovered);
    }

    private async Task PublishProductAsync(ProductUpserted message)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<MassTransit.IPublishEndpoint>();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        await bus.Publish(message);
        await db.SaveChangesAsync(); // flush the transactional outbox
    }

    private async Task<ThreeCommerce.Ordering.Domain.ProductCopy> WaitForProductCopyAsync(
        Guid productId, Func<ThreeCommerce.Ordering.Domain.ProductCopy, bool> matches)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var copy = await db.ProductCopies.FindAsync(productId);
            if (copy is not null && matches(copy))
            {
                return copy;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"ProductCopy {productId} did not reach the expected state.");
    }

    private async Task PublishAsync(StorefrontConfigChanged message)
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<MassTransit.IPublishEndpoint>();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        await bus.Publish(message);
        await db.SaveChangesAsync(); // flush the transactional outbox
    }

    private async Task<ThreeCommerce.Ordering.Domain.StorefrontTaxCopy> WaitForCopyAsync(
        Guid storefrontId, Func<ThreeCommerce.Ordering.Domain.StorefrontTaxCopy, bool> matches)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            var copy = await db.StorefrontTaxCopies.FindAsync(storefrontId);
            if (copy is not null && matches(copy))
            {
                return copy;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"StorefrontTaxCopy {storefrontId} did not reach the expected state.");
    }
}
