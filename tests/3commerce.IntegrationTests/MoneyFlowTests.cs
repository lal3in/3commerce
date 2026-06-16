using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// The Phase-3 money pipeline end to end across Ordering + Payments (fake provider):
/// cart → checkout saga → ledger → confirmation, plus refund and webhook idempotency.
/// FR-3/4/5, NFR-1/3.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public class MoneyFlowTests(Phase3Fixture fixture)
{
    private sealed record CheckoutResponseDto(Guid OrderId, string ClientSecret, long NetMinor, long TaxMinor, long GrossMinor, string Currency, string? Message);
    private sealed record StatusDto(Guid Id, string Status);

    private static object Checkout() => new
    {
        email = "buyer@example.com",
        shippingAddress = new { name = "B", line1 = "1 St", city = "Berlin", postcode = "10115", country = "DE" },
    };

    [Fact]
    public async Task Guest_checkout_confirms_and_posts_a_balanced_sale()
    {
        var productId = await fixture.SeedProductAsync(10_000);
        using var shopper = fixture.Ordering.CreateClient();

        // Add to cart (cookie persists the anonymous cart).
        var add = await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 2 });
        add.EnsureSuccessStatusCode();

        var checkout = await shopper.PostAsJsonAsync("/checkout", Checkout());
        checkout.EnsureSuccessStatusCode();
        var order = (await checkout.Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        // net = 2×10000 + 499 shipping = 20499; tax 19% = 3895; gross 24394.
        Assert.Equal(20_499, order.NetMinor);
        Assert.Equal(3_895, order.TaxMinor);
        Assert.Equal(24_394, order.GrossMinor);
        Assert.StartsWith("pi_fake_", order.ClientSecret);

        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task Duplicate_payment_webhook_posts_one_journal_entry()
    {
        var productId = await fixture.SeedProductAsync(5_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;

        var before = await fixture.TrialBalanceAsync();
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor); // same event id → deduped
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        // Exactly one sale entry was posted (trial balance stays zero; entry count is one).
        Assert.Equal(0, await fixture.TrialBalanceAsync());
        Assert.Equal(0, before);
        Assert.Equal(1, await CountEntriesForAsync(order.OrderId));
    }

    [Fact]
    public async Task Refund_reverses_the_sale_and_keeps_the_ledger_balanced()
    {
        var productId = await fixture.SeedProductAsync(8_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");

        // Publish a refund directly (same contract the admin endpoint / Phase-4 RMA use).
        using (var scope = fixture.Payments.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<MassTransit.IPublishEndpoint>();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            await bus.Publish(new ThreeCommerce.BuildingBlocks.Contracts.Payments.RefundRequested(
                Guid.CreateVersion7(), order.OrderId, order.GrossMinor, "test", "admin"));
            await db.SaveChangesAsync();
        }

        await WaitForRefundAsync(order.OrderId);
        Assert.Equal(0, await fixture.TrialBalanceAsync());
    }

    [Fact]
    public async Task Checkout_saga_survives_an_ordering_outage_during_payment()
    {
        // NFR-2 chaos: the saga host dies after checkout but before the payment lands.
        var productId = await fixture.SeedProductAsync(7_000);
        using (var shopper = fixture.Ordering.CreateClient())
        {
            await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
            var pending = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
            await WaitForSagaAsync(pending.OrderId); // saga durably awaiting payment

            // Outage: Ordering (the saga owner) goes down.
            await fixture.RestartOrderingAsync();

            // Payment succeeds while Ordering is restarting — PaymentSucceeded queues durably.
            using var payments = fixture.Payments.CreateClient();
            var intentId = $"pi_fake_{pending.OrderId:N}";
            (await payments.PostAsync($"/dev/simulate-payment/{intentId}?amountMinor={pending.GrossMinor}", null)).EnsureSuccessStatusCode();

            // The restarted host drains the queue and the saga still reaches Confirmed.
            using var recovered = fixture.Ordering.CreateClient();
            await WaitForStatusAsync(recovered, pending.OrderId, "Confirmed");
            Assert.Equal(0, await fixture.TrialBalanceAsync());
        }
    }

    [Fact]
    public async Task Checkout_emits_one_distributed_trace_across_the_http_and_message_hops()
    {
        // NFR-7: the HTTP-initiated checkout trace must carry through the async message
        // hops (MassTransit propagates context through the outbox), so the same TraceId
        // spans the AspNetCore entry span and the saga/consume spans.
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var productId = await fixture.SeedProductAsync(6_000);
        using var shopper = fixture.Ordering.CreateClient();
        await shopper.PostAsJsonAsync("/cart/items", new { productId, quantity = 1 });
        var order = (await (await shopper.PostAsJsonAsync("/checkout", Checkout())).Content.ReadFromJsonAsync<CheckoutResponseDto>())!;
        await SimulatePaymentAsync(order.OrderId, order.GrossMinor);
        await WaitForStatusAsync(shopper, order.OrderId, "Confirmed");
        await Task.Delay(500); // let the last consume spans flush

        // A single trace must contain both an HTTP server span and a MassTransit span.
        var byTrace = activities
            .Where(a => a.TraceId != default)
            .GroupBy(a => a.TraceId);
        var correlated = byTrace.FirstOrDefault(g =>
            g.Any(a => a.Source.Name.StartsWith("Microsoft.AspNetCore")) &&
            g.Any(a => a.Source.Name == "MassTransit"));

        Assert.True(correlated is not null,
            $"no trace spanned both HTTP and MassTransit; saw sources: " +
            string.Join(", ", activities.Select(a => a.Source.Name).Distinct().OrderBy(s => s)));
    }

    private async Task SimulatePaymentAsync(Guid orderId, long gross)
    {
        // In reality the client confirms payment seconds after checkout; wait for the saga
        // to have started (CartSubmitted delivered via the outbox) so the success isn't dropped.
        await WaitForSagaAsync(orderId);

        using var payments = fixture.Payments.CreateClient();
        var intentId = $"pi_fake_{orderId:N}";
        var r = await payments.PostAsync($"/dev/simulate-payment/{intentId}?amountMinor={gross}", null);
        r.EnsureSuccessStatusCode();
    }

    private async Task WaitForSagaAsync(Guid orderId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>();
            if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.CheckoutStates.Where(s => s.CorrelationId == orderId)))
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"Checkout saga for {orderId} did not start.");
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

    private async Task<int> CountEntriesForAsync(Guid orderId)
    {
        using var scope = fixture.Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
            db.JournalEntries.Where(e => e.Reference == orderId.ToString()));
    }

    private async Task WaitForRefundAsync(Guid orderId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = fixture.Payments.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
            if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                    db.Refunds.Where(r => r.OrderId == orderId)))
            {
                return;
            }

            await Task.Delay(300);
        }

        throw new TimeoutException($"Refund for order {orderId} was not processed.");
    }
}
