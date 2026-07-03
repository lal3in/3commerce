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

        await PublishAsync(new StorefrontConfigChanged(storefrontId, tenantId, "NZ Store", "NZD", 1_500, IsLive: true));
        var created = await WaitForCopyAsync(storefrontId, c => c.TaxRateBasisPoints == 1_500 && c.IsLive);
        Assert.Equal("NZD", created.Currency);

        // Update: rate change + taken offline → same row updated, not duplicated.
        await PublishAsync(new StorefrontConfigChanged(storefrontId, tenantId, "NZ Store", "NZD", 1_250, IsLive: false));
        var updated = await WaitForCopyAsync(storefrontId, c => c.TaxRateBasisPoints == 1_250 && !c.IsLive);
        Assert.Equal("NZD", updated.Currency);
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
