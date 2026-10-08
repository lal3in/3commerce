using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Tools.OrderConfirmedBackfill;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0060 backfill, end to end on real Postgres + RabbitMQ: a real checkout confirms through Ordering, which
/// PUBLISHES <see cref="OrderConfirmed"/>; the backfill tool reloads that order from Ordering's database (its own
/// loader, inside the RLS tenant scope) and rebuilds the event — it must equal what was published. Then the tool
/// SENDS it to <c>queue:fulfillment-order-confirmed</c>: exactly that queue receives it, with the deterministic
/// backfill message id, and a subscriber of the published event gets no second copy.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class OrderConfirmedBackfillTests(Phase3Fixture fixture)
{
    private sealed record CheckoutResponseDto(Guid OrderId, long GrossMinor);
    private sealed record StatusDto(Guid Id, string Status);

    [Fact]
    public async Task Rebuilt_event_equals_the_published_one_and_a_send_reaches_only_the_target_queue()
    {
        var published = new ConcurrentQueue<OrderConfirmed>();
        var sent = new ConcurrentQueue<(OrderConfirmed Message, Guid? MessageId, string? Header)>();

        // Two probes on the fixture's broker: one subscribed to the OrderConfirmed exchange (what every live consumer
        // sees), one on the Fulfillment queue name WITHOUT exchange bindings (it can only get what is sent to it).
        // The Phase3 fixture runs no Fulfillment host, so nothing else consumes that queue here.
        var probe = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(fixture.RabbitMqUri));
            cfg.ReceiveEndpoint("backfill-probe-published-order-confirmed", e =>
                e.Handler<OrderConfirmed>(ctx =>
                {
                    published.Enqueue(ctx.Message);
                    return Task.CompletedTask;
                }));
            cfg.ReceiveEndpoint(BackfillQueues.QueueName(BackfillTarget.Fulfillment), e =>
            {
                e.ConfigureConsumeTopology = false;
                e.Handler<OrderConfirmed>(ctx =>
                {
                    sent.Enqueue((ctx.Message, ctx.MessageId, ctx.Headers.Get<string>(BackfillQueues.BackfillHeader)));
                    return Task.CompletedTask;
                });
            });
        });
        await probe.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        try
        {
            // A real order: a dropship line with a supplier + an unassigned line, through checkout and payment.
            var (supplied, supplierId) = await fixture.SeedSuppliedProductAsync(2_500, 900, fulfilmentType: FulfilmentType.Dropship);
            var plain = await fixture.SeedProductAsync(1_200);
            using var shopper = fixture.Ordering.CreateClient();
            (await shopper.PostAsJsonAsync("/cart/items", new { productId = supplied, quantity = 2 })).EnsureSuccessStatusCode();
            (await shopper.PostAsJsonAsync("/cart/items", new { productId = plain, quantity = 1 })).EnsureSuccessStatusCode();
            var checkout = await shopper.PostAsJsonAsync("/checkout", new
            {
                email = "backfill-buyer@example.com",
                shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
            });
            checkout.EnsureSuccessStatusCode();
            var order = (await checkout.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
            await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
            await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
            var live = await WaitForAsync(published, m => m.OrderId == order.OrderId, "the published OrderConfirmed");

            // The backfill path: the tool's loader against Ordering's real database, then the shared factory.
            OrderConfirmed rebuilt;
            using (var scope = fixture.Ordering.Services.CreateScope())
            {
                var source = new OrderSource(scope.ServiceProvider.GetRequiredService<OrderingDbContext>(), new RlsTenantScope());
                var orders = await source.ConfirmedOrdersAsync(live.TenantId, default);
                rebuilt = OrderConfirmedFactory.From(orders.Single(o => o.Id == order.OrderId));
            }

            Assert.Equal(Canonical(live), Canonical(rebuilt));
            Assert.Contains(rebuilt.Lines, l => l.FulfilmentType == FulfilmentType.Dropship && l.SupplierId == supplierId);

            // Point-to-point: the Fulfillment queue gets it, with the backfill id + header; the subscriber does not.
            await new BackfillSender(probe).SendAsync(BackfillTarget.Fulfillment, [rebuilt], null, default);
            var delivered = await WaitForAsync(sent, s => s.Message.OrderId == order.OrderId, "the backfilled send");
            Assert.Equal(BackfillQueues.MessageId(BackfillTarget.Fulfillment, order.OrderId), delivered.MessageId);
            Assert.Equal(BackfillQueues.BackfillHeaderValue, delivered.Header);
            Assert.Equal(Canonical(rebuilt), Canonical(delivered.Message));

            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Single(published, m => m.OrderId == order.OrderId);
        }
        finally
        {
            await probe.StopAsync();
        }
    }

    /// <summary>JSON with lines in a fixed order — both paths order lines by their (per-millisecond random) ids.</summary>
    private static string Canonical(OrderConfirmed e) =>
        JsonSerializer.Serialize(e with { Lines = e.Lines.OrderBy(l => l.ProductId).ToList() });

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

    private async Task SimulatePaymentAsync(Guid orderId, long gross)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.CheckoutStates.Where(s => s.CorrelationId == orderId)))
            {
                break;
            }

            await Task.Delay(250);
        }

        using var payments = fixture.Payments.CreateClient();
        (await payments.PostAsync($"/dev/simulate-payment/pi_fake_{orderId:N}?amountMinor={gross}", null)).EnsureSuccessStatusCode();
    }

    private static async Task WaitForStatusAsync(HttpClient client, Guid orderId, string expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<StatusDto>($"/orders/{orderId}/status");
            if (status?.Status == expected)
            {
                return;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"Order {orderId} did not reach {expected}.");
    }
}
