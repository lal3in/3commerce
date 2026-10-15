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
/// End to end through the REAL Payments outbox, with the outbox busy: each round stages a 30-message batch, then
/// submits ("not active") and activates ("active") a payment account. Two guarantees, two bugs:
/// <list type="number">
/// <item>One host delivers its outbox batches one transaction at a time, in publish order
/// (<c>MassTransitExtensions.OutboxDeliveryQueryLimit</c>). At MassTransit's default the delivery service ran up to six
/// transactions in parallel after a busy pass: batches reached Catalog inverted (1–5 of 20 storefronts per run), and
/// the parallel transactions collided on the outbox lock (40001), stalling every pending message 3–30 s.</item>
/// <item>Catalog ends on "active" even when the events do arrive inverted (replicas still deliver in parallel): the
/// readiness events carry AsOf and Catalog keeps the newer copy (ADR-0043 amendment 2026-10-16).</item>
/// </list>
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
    public async Task A_busy_outbox_delivers_submit_then_activate_in_order_and_catalog_ends_active()
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
            $"{stuck.Count}/{Rounds} storefronts stuck on 'not active' in Catalog ({inverted} saw the events inverted):"
            + string.Concat(await Task.WhenAll(stuck.Select(DescribeAsync))));
        Assert.True(inverted == 0,
            $"{inverted}/{Rounds} storefronts received 'active' before 'not active': one host's outbox delivered out of publish order"
            + string.Concat(await Task.WhenAll(storefronts.Where(_arrivals.ArrivedOutOfOrder).Select(DescribeAsync))));
    }

    public void Dispose() => _handle?.Disconnect();

    // Stages Burst readiness events for throw-away storefronts in ONE Payments outbox batch: the next delivery pass
    // sends 10+ messages, which is what made MassTransit's default go parallel for the passes that follow.
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

    // One stuck storefront: what Catalog stored (flag + AsOf) and every delivery it saw, in order, with any fault.
    private async Task<string> DescribeAsync(Guid storefront)
    {
        using var scope = fixture.Catalog.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var row = await db.StorefrontPaymentReadiness.AsNoTracking().SingleOrDefaultAsync(r => r.StorefrontId == storefront);
        var stored = row is null ? "no row" : $"stored active={row.HasActivePaymentAccount} asOf={row.AsOf:O}";
        return $"{Environment.NewLine}  {storefront}: {stored}; deliveries: {_arrivals.Describe(storefront)}";
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
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<(bool Active, string Event)>> _arrivals = new();

        public bool ArrivedOutOfOrder(Guid storefront) =>
            _arrivals.TryGetValue(storefront, out var events)
            && events.Where(e => e.Event == "consume").Select(e => e.Active).ToArray() is [.., true, false];

        public string Describe(Guid storefront) =>
            _arrivals.TryGetValue(storefront, out var events) ? string.Join(" | ", events.Select(e => e.Event)) : "none";

        public Task PreConsume<T>(ConsumeContext<T> context) where T : class
        {
            Record(context, "consume");
            return Task.CompletedTask;
        }

        public Task PostConsume<T>(ConsumeContext<T> context) where T : class
        {
            Record(context, "ok");
            return Task.CompletedTask;
        }

        public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
        {
            Record(context, $"FAULT {exception.GetType().Name}: {exception.Message}");
            return Task.CompletedTask;
        }

        private void Record<T>(ConsumeContext<T> context, string what) where T : class
        {
            if (context.Message is StorefrontPaymentReadinessChanged m)
            {
                var detail = what == "consume"
                    ? "consume"
                    : $"{what} (active={m.HasActivePaymentAccount} asOf={m.AsOf:O} id={context.MessageId} retry={context.GetRetryAttempt()})";
                _arrivals.GetOrAdd(m.StorefrontId, _ => new()).Enqueue((m.HasActivePaymentAccount, detail));
            }
        }
    }
}
