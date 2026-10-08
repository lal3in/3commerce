using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Domain;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Fulfillment.Domain;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Tools.OrderConfirmedBackfill;
using ThreeCommerce.Tools.OrderConfirmedBackfill.Storefronts;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0060 storefront backfill, end to end on real Postgres + RabbitMQ: a real duplication through Catalog's endpoint
/// PUBLISHES <see cref="StorefrontDuplicated"/> (and its <c>catalog.storefront.duplicate</c> audit entry). The tool reads
/// the storefronts and publications back from Catalog's database (its own loader, inside the RLS tenant scope), links
/// the duplicate to its source by the copied publications, and rebuilds the event — it must equal what was published.
/// Then it SENDS it to Payments' <c>queue:storefront-duplicated</c> only: that queue receives it with the deterministic
/// backfill message id, Fulfillment's queue gets nothing, and a subscriber of the published event gets no second copy.
/// The Phase 2 fixture runs neither Payments nor Fulfillment, so nothing else consumes those queue names here; their
/// rows are given as facts (the copy that DID run reached Fulfillment).
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase2Collection.Name)]
public class StorefrontDuplicatedBackfillTests(Phase2Fixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantId = new("00000000-0000-0000-0000-000000000001");

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> _catalog = null!;
    private HttpClient _admin = null!;
    private Guid _categoryId;

    private sealed record StorefrontDto(Guid Id, string Name);
    private sealed record EditorDto(Guid Id);
    private sealed record VariantDto(Guid? Id, string Sku, long PriceMinor, string? Currency, int StockQuantity,
        int? WeightGrams = 500, int? LengthMm = 200, int? WidthMm = 150, int? HeightMm = 100,
        int? PackageWeightGrams = 650, int? PackageLengthMm = 250, int? PackageWidthMm = 200, int? PackageHeightMm = 150);

    public async Task InitializeAsync()
    {
        _catalog = fixture.CreateCatalogFactory();
        _admin = _catalog.CreateClient();
        _admin.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), Roles.Admin));

        using var scope = _catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        _categoryId = Guid.CreateVersion7();
        db.Categories.Add(new Category { Id = _categoryId, TenantId = TenantId, Slug = $"sfdup-{_categoryId:N}", Name = "SfDup" });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        _catalog.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Rebuilt_event_equals_the_published_one_and_a_send_reaches_only_the_payments_queue()
    {
        var published = new ConcurrentQueue<StorefrontDuplicated>();
        var audited = new ConcurrentQueue<AuditEntryRecorded>();
        var sent = new ConcurrentQueue<(StorefrontBackfillTarget Queue, StorefrontDuplicated Message, Guid? MessageId, string? Header)>();

        var probe = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(fixture.RabbitMqUri));
            cfg.ReceiveEndpoint("sfdup-backfill-probe-published", e =>
            {
                e.Handler<StorefrontDuplicated>(ctx =>
                {
                    published.Enqueue(ctx.Message);
                    return Task.CompletedTask;
                });
                e.Handler<AuditEntryRecorded>(ctx =>
                {
                    audited.Enqueue(ctx.Message);
                    return Task.CompletedTask;
                });
            });
            // The two consumers' queue names WITHOUT exchange bindings: they only get what is sent to them.
            foreach (var target in new[] { StorefrontBackfillTarget.Payments, StorefrontBackfillTarget.Fulfillment })
            {
                cfg.ReceiveEndpoint(StorefrontBackfillQueues.QueueName(target), e =>
                {
                    e.ConfigureConsumeTopology = false;
                    e.Handler<StorefrontDuplicated>(ctx =>
                    {
                        sent.Enqueue((target, ctx.Message, ctx.MessageId, ctx.Headers.Get<string>(BackfillQueues.BackfillHeader)));
                        return Task.CompletedTask;
                    });
                });
            }
        });
        await probe.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        try
        {
            // A real source: a storefront with a published product, duplicated through Catalog's endpoint.
            var sourceName = $"SfDup-{Guid.NewGuid():N}"[..20];
            var source = await CreateStorefrontAsync(sourceName);
            await PublishProductAsync(source);
            var duplicate = await _admin.PostAsJsonAsync($"/admin/storefronts/{source}/duplicate", new { name = $"{sourceName} (copy)" });
            Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
            var clone = (await duplicate.Content.ReadFromJsonAsync<StorefrontDto>())!.Id;
            var live = await WaitForAsync(published, m => m.NewStorefrontId == clone, "the published StorefrontDuplicated");
            var entry = await WaitForAsync(audited, a => a.Action == DuplicationAuditSource.DuplicateAction && a.ResourceId == clone.ToString(), "the duplicate audit entry");

            // The backfill path: the tool's Catalog loader against the real database, the audit entry as the Audit
            // service projects it, and the services' rows as facts — the source has a payment account and a carrier;
            // the copy that DID run reached Fulfillment (the duplicate holds a clone of the carrier), Payments' did not.
            StorefrontTenantSnapshot snapshot;
            using (var scope = _catalog.Services.CreateScope())
            {
                var catalog = new StorefrontCatalogSource(scope.ServiceProvider.GetRequiredService<CatalogDbContext>(), new RlsTenantScope());
                var storefronts = await catalog.StorefrontsAsync(TenantId, default);
                var publications = await catalog.PublicationsAsync(TenantId, default);
                var sourceFact = storefronts.Single(s => s.Id == source);
                var cloneFact = storefronts.Single(s => s.Id == clone);
                var carrier = CarrierIntegration.Configure(TenantId, source, CarrierCode.Fake, null, sourceFact.CreatedAt);
                snapshot = new StorefrontTenantSnapshot(
                    TenantId,
                    storefronts,
                    [new DuplicationFact(entry.TenantId, Guid.Parse(entry.ResourceId), entry.Summary),
                     .. StorefrontBackfillPlanner.ProvenByPublications(TenantId, storefronts, publications)],
                    publications,
                    [ConfigRowFact.From(PaymentAccount.Create(TenantId, source, "Card", "mock", PaymentProviderMode.Test, true, null, sourceFact.CreatedAt))],
                    [ConfigRowFact.From(carrier), ConfigRowFact.From(carrier.CloneForStorefront(clone, cloneFact.CreatedAt.AddMilliseconds(50)))]);
            }

            var verdict = StorefrontBackfillPlanner.Plan(snapshot, StorefrontBackfillPlanner.DefaultCloneWindow).Single(v => v.TargetId == clone);
            Assert.Equal(new SideVerdict(SideOutcome.Missing, source), verdict.Payments);
            Assert.Equal(SideVerdict.Present, verdict.Fulfillment);
            Assert.StartsWith("publications", verdict.Evidence, StringComparison.Ordinal);

            var rebuilt = StorefrontBackfillQueues.Rebuild(verdict, StorefrontBackfillTarget.Payments);
            Assert.Equal(live, rebuilt);

            // Point-to-point: only Payments' queue gets it, with the backfill id + header; the subscriber does not.
            await new StorefrontBackfillSender(probe).SendAsync(StorefrontBackfillTarget.Payments, [rebuilt], default);
            var delivered = await WaitForAsync(sent, s => s.Message.NewStorefrontId == clone, "the backfilled send");
            Assert.Equal(StorefrontBackfillTarget.Payments, delivered.Queue);
            Assert.Equal(StorefrontBackfillQueues.MessageId(StorefrontBackfillTarget.Payments, clone), delivered.MessageId);
            Assert.Equal(StorefrontBackfillQueues.BackfillHeaderValue, delivered.Header);
            Assert.Equal(rebuilt, delivered.Message);

            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Single(sent, s => s.Message.NewStorefrontId == clone);
            Assert.Single(published, m => m.NewStorefrontId == clone);
        }
        finally
        {
            await probe.StopAsync();
        }
    }

    private async Task<Guid> CreateStorefrontAsync(string name)
    {
        var response = await _admin.PostAsJsonAsync("/admin/storefronts", new { tenantId = TenantId, name, visibility = 1, currency = "EUR" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StorefrontDto>())!.Id;
    }

    private async Task PublishProductAsync(Guid storefrontId)
    {
        var create = await _admin.PostAsJsonAsync("/admin/products", new
        {
            slug = $"sfdup-p-{Guid.NewGuid():N}",
            title = "SfDup Product",
            brand = "Acme",
            description = "x",
            categoryId = _categoryId,
            attributes = new Dictionary<string, string>(),
            imageUrls = new[] { "https://example.test/img.png" },
            variants = new[] { new VariantDto(null, $"SFD-{Guid.NewGuid():N}"[..12], 2_000, "EUR", 5) },
        });
        create.EnsureSuccessStatusCode();
        var productId = (await create.Content.ReadFromJsonAsync<EditorDto>())!.Id;
        (await _admin.PostAsJsonAsync($"/admin/storefronts/{storefrontId}/products",
            new { productId, fulfillmentSource = 2, countryOfOrigin = "AU" })).EnsureSuccessStatusCode();
        (await _admin.PostAsync($"/admin/storefronts/{storefrontId}/products/{productId}/publish", null)).EnsureSuccessStatusCode();
    }

    private static async Task<T> WaitForAsync<T>(ConcurrentQueue<T> queue, Func<T, bool> match, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var item in queue)
            {
                if (match(item))
                {
                    return item;
                }
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }
}
