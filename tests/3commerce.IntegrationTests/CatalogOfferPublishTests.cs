using System.Net.Http.Json;
using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Regression (ADR-0008): the Catalog offer endpoints must DELIVER OfferChanged to the broker, not
/// just stage it. They publish through the EF bus outbox, which only ships on SaveChanges — so a
/// publish AFTER the final SaveChanges is stranded in the change tracker and never sent. That exact
/// bug left Ordering's OfferCopy projection empty for every offer, so subscription/usage lines
/// silently checked out as OneTime/Once and no subscription was ever set up. This asserts a created
/// subscription offer actually reaches RabbitMQ carrying its recurring pricing.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class CatalogOfferPublishTests(Phase2Fixture fixture)
{
    [Fact]
    public async Task Creating_a_subscription_offer_delivers_OfferChanged_to_the_broker()
    {
        var catalog = fixture.CreateCatalogFactory();
        var admin = catalog.CreateClient();
        admin.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin));

        var productId = Guid.CreateVersion7();
        var received = new TaskCompletionSource<OfferChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(fixture.RabbitMqUri));
            cfg.ReceiveEndpoint($"offer-changed-probe-{Guid.NewGuid():N}", e =>
            {
                e.AutoDelete = true;
                e.Durable = false;
                e.Handler<OfferChanged>(ctx =>
                {
                    if (ctx.Message.ProductId == productId)
                    {
                        received.TrySetResult(ctx.Message);
                    }

                    return Task.CompletedTask;
                });
            });
        });
        await probe.StartAsync();
        try
        {
            var response = await admin.PostAsJsonAsync("/admin/offers", new
            {
                productId,
                supplierId = Guid.CreateVersion7(),
                supplyCategory = (int)SupplyCategory.Digital,
                fulfilmentType = (int)FulfilmentType.Subscription,
                priceMinor = 999L,
                currency = "EUR",
                priority = 10,
                pricingModel = (int)PricingModel.Subscription,
                billingPeriod = (int)BillingPeriod.Monthly,
            });
            response.EnsureSuccessStatusCode();

            // Without the fix this never arrives (stranded in the uncommitted outbox row).
            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(PricingModel.Subscription, message.PricingModel);
            Assert.Equal(BillingPeriod.Monthly, message.BillingPeriod);
        }
        finally
        {
            await probe.StopAsync();
        }
    }
}
