using System.Collections.Concurrent;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Payments.Infrastructure;
using Xunit.Abstractions;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// End to end through the REAL Payments outbox, with the outbox busy: each round stages a 30-message batch, then
/// submits ("not active") and activates ("active") a payment account. After a busy pass MassTransit delivers up to six
/// outbox batches in parallel, so the two readiness events can reach Catalog inverted (1–5 of 20 storefronts per run).
/// Two guarantees, two bugs:
/// <list type="number">
/// <item>Catalog ends on "active" whatever the arrival order: the events carry AsOf and Catalog keeps the newer copy
/// (ADR-0043 amendment 2026-10-16). Without the guard 3 of 5 runs left a storefront stuck on "not active".</item>
/// <item>The parallel delivery transactions never fail: they run READ COMMITTED
/// (<c>MassTransitExtensions.OutboxDeliveryIsolation</c>). Under REPEATABLE READ they collided on the outbox lock with
/// 40001 and the delivery backed off 3–30 s, which is how projections ended up 20–30 s late.</item>
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
    private readonly ErrorLogCapture _paymentsErrors = new();
    private ConnectHandle? _handle;

    [Fact]
    public async Task A_busy_outbox_delivers_without_serialization_failures_and_catalog_ends_active_whatever_the_order()
    {
        _handle = fixture.Catalog.Services.GetRequiredService<IBus>().ConnectConsumeObserver(_arrivals);
        fixture.Payments.Services.GetRequiredService<ILoggerFactory>().AddProvider(_paymentsErrors);
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
        var serializationFailures = _paymentsErrors.Matching("40001");
        Assert.True(serializationFailures.Count == 0,
            $"Payments logged {serializationFailures.Count} serialization failure(s) while delivering its outbox:"
            + Environment.NewLine + string.Join(Environment.NewLine, serializationFailures.Take(3)));
    }

    public void Dispose()
    {
        _handle?.Disconnect();
        _paymentsErrors.Stop(); // a logger provider cannot be removed from the shared host; stop it recording
    }

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

    /// <summary>Error-or-worse log lines (message + exception) of the host it is added to, while recording.</summary>
    private sealed class ErrorLogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();
        private volatile bool _recording = true;

        public List<string> Matching(string signature) =>
            _entries.Where(e => e.Contains(signature, StringComparison.Ordinal)).ToList();

        public void Stop() => _recording = false;

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ErrorLogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => owner._recording && logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    owner._entries.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)} {exception}");
                }
            }
        }
    }
}
