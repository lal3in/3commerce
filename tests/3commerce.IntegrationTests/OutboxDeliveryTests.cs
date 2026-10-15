using MassTransit;
using MassTransit.Util;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Unit lane: every service's bus-outbox delivery runs ONE delivery transaction at a time. MassTransit's
/// <c>BusOutboxDeliveryService</c> drives its passes through a <see cref="RequestRateAlgorithm"/> built from
/// <c>QueryMessageLimit</c> (PrefetchCount) and a fixed RequestResultLimit of 10. At MassTransit's default of 100 it
/// ran up to six transactions at once after a busy pass; under REPEATABLE READ they collided on the outbox lock
/// (<c>40001 … concurrent update</c>), the delivery backed off 3–30 s and every pending message of the service was
/// late (StorefrontReadinessCrossServiceTests, AuditTimelineProjectionTests timing out, 2026-10-15/16), and parallel
/// batches reached consumers out of publish order.
/// </summary>
public sealed class OutboxDeliveryTests
{
    [Fact]
    public void AddServiceBus_sizes_outbox_delivery_for_one_transaction_at_a_time()
    {
        var services = new ServiceCollection();
        services.AddServiceBus<OutboxDeliveryDbContext>(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<OutboxDeliveryServiceOptions>>().Value;
        Assert.Equal(MassTransitExtensions.OutboxDeliveryQueryLimit, options.QueryMessageLimit);
    }

    [Fact]
    public async Task At_the_configured_limit_deliveries_never_run_in_parallel_and_at_the_default_they_do()
    {
        // The default is the control: it proves the algorithm does go parallel under load, so the 1 below is the
        // configured limit's doing and not an artefact of this harness.
        Assert.True(await MaxParallelDeliveriesAsync(queryMessageLimit: 100) > 1);
        Assert.Equal(1, await MaxParallelDeliveriesAsync(MassTransitExtensions.OutboxDeliveryQueryLimit));
    }

    // Mirrors BusOutboxDeliveryService: a RequestRateAlgorithm(PrefetchCount = QueryMessageLimit, RequestResultLimit = 10)
    // whose callback is one delivery transaction. Every pass reports a full batch (a busy outbox), which is what
    // raises the parallelism.
    private static async Task<int> MaxParallelDeliveriesAsync(int queryMessageLimit)
    {
        using var algorithm = new RequestRateAlgorithm(new RequestRateAlgorithmOptions
        {
            PrefetchCount = queryMessageLimit,
            RequestResultLimit = 10,
        });
        var active = 0;
        var max = 0;
        for (var pass = 0; pass < 6; pass++)
        {
            await algorithm.Run(async (resultLimit, _) =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedMax(ref max, now);
                await Task.Delay(20);
                Interlocked.Decrement(ref active);
                return resultLimit;
            });
        }

        return max;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private sealed class OutboxDeliveryDbContext(DbContextOptions<OutboxDeliveryDbContext> options) : DbContext(options);
}
