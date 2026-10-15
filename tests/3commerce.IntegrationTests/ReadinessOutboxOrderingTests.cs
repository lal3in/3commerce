using System.Collections.Concurrent;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Payments.Infrastructure;
using Xunit.Abstractions;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// End to end through the REAL Payments outbox: the bus outbox does not deliver in publish order. Each admin
/// transition is its own outbox batch, and once a delivery pass has sent 10+ messages MassTransit's delivery service
/// (RequestRateAlgorithm) sends up to six batches in parallel, so a payment account's "submitted" (not active) and
/// "activated" (active) readiness events can reach Catalog in either order. Catalog must still end on "active"
/// (ADR-0042, AsOf guard). Each round bursts the outbox first, the way a busy service does, then drives the two
/// transitions; the arrival order Catalog saw is recorded, so the log shows how often the events were inverted.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public sealed class ReadinessOutboxOrderingTests(Phase4Fixture fixture, ITestOutputHelper output) : IDisposable
{
    private const int Rounds = 20;
    private const int Burst = 30;

    private sealed record AccountDto(Guid Id);

    private readonly ArrivalObserver _arrivals = new();
    private ConnectHandle? _handle;

    [Fact]
    public async Task Submitted_then_activated_payment_accounts_end_active_in_catalog_whatever_order_the_outbox_delivers()
    {
        _handle = fixture.Catalog.Services.GetRequiredService<IBus>().ConnectConsumeObserver(_arrivals);
        using var admin = fixture.Payments.CreateClient();
        admin.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));

        var tenant = Guid.CreateVersion7();
        var storefronts = new List<Guid>();
        for (var round = 0; round < Rounds; round++)
        {
            var storefront = Guid.CreateVersion7();
            storefronts.Add(storefront);
            var account = await (await admin.PostAsJsonAsync("/admin/payment-accounts",
                    new { tenantId = tenant, storefrontId = storefront, name = "Main", provider = "stripe", mode = 1, isDefaultForStorefront = true }))
                .Content.ReadFromJsonAsync<AccountDto>();

            await BurstAsync(tenant);
            (await admin.PostAsync($"/admin/payment-accounts/{account!.Id}/submit", null)).EnsureSuccessStatusCode();
            (await admin.PostAsync($"/admin/payment-accounts/{account.Id}/activate", null)).EnsureSuccessStatusCode();
        }

        var stuck = new List<Guid>();
        await Eventually.PollAsync(async () =>
        {
            stuck = await NotActiveAsync(storefronts);
            return stuck.Count == 0;
        });

        var inverted = storefronts.Count(_arrivals.ArrivedOutOfOrder);
        output.WriteLine($"{inverted}/{Rounds} storefronts received 'active' before 'not active' (outbox delivered out of order).");
        Assert.True(stuck.Count == 0,
            $"{stuck.Count}/{Rounds} storefronts stuck on 'not active' in Catalog ({inverted} saw the events inverted): {string.Join(", ", stuck)}");
    }

    public void Dispose() => _handle?.Disconnect();

    // Stages Burst readiness events for throw-away storefronts in ONE Payments outbox batch: the next delivery pass
    // sends 10+ messages, which raises the delivery service's parallelism for the passes that follow.
    private async Task BurstAsync(Guid tenant)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        for (var i = 0; i < Burst; i++)
        {
            await publisher.Publish(new StorefrontPaymentReadinessChanged(tenant, Guid.CreateVersion7(), false, DateTimeOffset.UtcNow));
        }

        await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().SaveChangesAsync();
    }

    private async Task<List<Guid>> NotActiveAsync(List<Guid> storefronts)
    {
        using var scope = fixture.Catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var active = await db.StorefrontServiceReadiness.AsNoTracking()
            .Where(r => storefronts.Contains(r.StorefrontId) && r.HasActivePaymentAccount)
            .Select(r => r.StorefrontId)
            .ToListAsync();
        return storefronts.Except(active).ToList();
    }

    /// <summary>Records, per storefront, the order in which Catalog received payment readiness values.</summary>
    private sealed class ArrivalObserver : IConsumeObserver
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<bool>> _arrivals = new();

        public bool ArrivedOutOfOrder(Guid storefront) =>
            _arrivals.TryGetValue(storefront, out var values) && values.ToArray() is [.., true, false];

        public Task PreConsume<T>(ConsumeContext<T> context) where T : class
        {
            if (context.Message is StorefrontPaymentReadinessChanged m)
            {
                _arrivals.GetOrAdd(m.StorefrontId, _ => new ConcurrentQueue<bool>()).Enqueue(m.HasActivePaymentAccount);
            }

            return Task.CompletedTask;
        }

        public Task PostConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;

        public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class => Task.CompletedTask;
    }
}
