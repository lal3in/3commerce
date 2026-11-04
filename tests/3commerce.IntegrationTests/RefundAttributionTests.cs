using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0063 P5 over the real bus: a refund says which order lines its money is for. An RMA — customer-raised or
/// admin-opened — publishes <see cref="RefundRequested"/> with its lines (Ordering's line ids, carried by
/// <see cref="OrderConfirmed"/> into Support's snapshot), and Payments carries them onto <see cref="RefundCompleted"/>;
/// an admin amount-only refund carries none (kind PriceReduction). The ledger is unchanged: trial balance 0.
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase4Collection.Name)]
public sealed class RefundAttributionTests(Phase4Fixture fixture) : IAsyncLifetime
{
    private static readonly ShipToInfo Ship = new("Buyer", "1 Test St", "Paris", "75001", "FR");

    private readonly ConcurrentQueue<RefundRequested> _requested = new();
    private readonly ConcurrentQueue<RefundCompleted> _completed = new();
    private IBusControl? _probe;

    private sealed record RmaCreated(Guid RmaId);

    private sealed record RmaDtoRead(Guid Id);

    private sealed record RefundResponseDto(Guid RefundId);

    public async Task InitializeAsync()
    {
        _probe = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(fixture.RabbitMqUri));
            cfg.ReceiveEndpoint($"refund-attribution-probe-{Guid.NewGuid():N}", e =>
            {
                e.AutoDelete = true;
                e.Durable = false;
                e.Handler<RefundRequested>(ctx =>
                {
                    _requested.Enqueue(ctx.Message);
                    return Task.CompletedTask;
                });
                e.Handler<RefundCompleted>(ctx =>
                {
                    _completed.Enqueue(ctx.Message);
                    return Task.CompletedTask;
                });
            });
        });
        await _probe.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
    }

    public async Task DisposeAsync()
    {
        if (_probe is not null)
        {
            await _probe.StopAsync();
        }
    }

    [Fact]
    public async Task A_customer_rma_refund_carries_its_lines_from_request_to_completion()
    {
        var (orderId, keyboard, keyboardLine, _, _) = await ConfirmedOrderAsync();

        // One of two keyboards: 5000 listed less half the line's 700 discount = 4650 (ADR-0055 discounted basis).
        using var customer = Client(fixture.Support, "customer");
        var response = await customer.PostAsJsonAsync("/rma", new
        {
            orderId,
            reason = "one keyboard damaged",
            lines = new[] { new { productId = keyboard, quantity = 1 } },
        });
        response.EnsureSuccessStatusCode();
        var rmaId = (await response.Content.ReadFromJsonAsync<RmaCreated>())!.RmaId;
        await WaitForStateAsync(rmaId, "Requested");

        using var admin = Client(fixture.Support, "admin");
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync($"/admin/rmas/{rmaId}/approve", new { requireReturn = false })).StatusCode);
        await WaitForStateAsync(rmaId, "RefundIssued");

        var refundId = await RefundIdAsync(rmaId);
        var completed = await CompletedAsync(refundId);
        var requested = _requested.Single(r => r.RefundId == refundId);

        Assert.Equal([new RefundLine(keyboardLine, 1, 4650)], requested.Lines!);
        Assert.Equal((4650L, 0L, RefundKind.Return), (requested.AmountMinor, requested.ShippingMinor!.Value, requested.Kind!.Value));
        Assert.Equal([new RefundLine(keyboardLine, 1, 4650)], completed.Lines!);
        Assert.Equal((4650L, 0L, RefundKind.Return), (completed.AmountMinor, completed.ShippingMinor!.Value, completed.Kind!.Value));
        Assert.Equal(0, await fixture.PaymentsTrialBalanceAsync());
    }

    [Fact]
    public async Task An_admin_whole_order_rma_attributes_every_line()
    {
        var (orderId, _, keyboardLine, _, lampLine) = await ConfirmedOrderAsync();

        using var admin = Client(fixture.Support, "admin");
        var response = await admin.PostAsJsonAsync("/admin/rmas", new { orderId, reason = "refunded by admin" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var rmaId = (await response.Content.ReadFromJsonAsync<RmaDtoRead>())!.Id;
        await WaitForStateAsync(rmaId, "RefundIssued");

        var completed = await CompletedAsync(await RefundIdAsync(rmaId));

        // 2 × 5000 − 700 = 9300 and 1 × 1900 = 1900; they sum to the amount refunded.
        Assert.Equal([new RefundLine(keyboardLine, 2, 9300), new RefundLine(lampLine, 1, 1900)], completed.Lines!.OrderByDescending(l => l.AmountMinor));
        Assert.Equal(11200, completed.AmountMinor);
        Assert.Equal(completed.AmountMinor, completed.Lines!.Sum(l => l.AmountMinor));
        Assert.Equal(0, await fixture.PaymentsTrialBalanceAsync());
    }

    [Fact]
    public async Task An_admin_amount_refund_carries_no_lines()
    {
        var orderId = Guid.CreateVersion7();
        await fixture.SeedSucceededPaymentAsync(orderId, grossMinor: 5000, taxMinor: 0);

        using var admin = Client(fixture.Payments, "admin");
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/refunds")
        {
            Content = JsonContent.Create(new { orderId, amountMinor = 500L, reason = "goodwill" }),
        };
        request.Headers.Add("Idempotency-Key", $"p5-{Guid.NewGuid():N}");
        var response = await admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var refundId = (await response.Content.ReadFromJsonAsync<RefundResponseDto>())!.RefundId;

        var completed = await CompletedAsync(refundId);

        // Negative control to the RMA tests above: no lines, no shipping attribution — an amount-only credit.
        Assert.Null(completed.Lines);
        Assert.Null(completed.ShippingMinor);
        Assert.Equal((500L, RefundKind.PriceReduction), (completed.AmountMinor, completed.Kind!.Value));
        Assert.Null(_requested.Single(r => r.RefundId == refundId).Lines);
        Assert.Equal(0, await fixture.PaymentsTrialBalanceAsync());
    }

    /// <summary>
    /// A captured order of keyboard 2 × 5000 (700 discount) + lamp 1 × 1900 = 11200, published as Ordering would so
    /// Support's own consumer projects the snapshot — line ids included.
    /// </summary>
    private async Task<(Guid OrderId, Guid Keyboard, Guid KeyboardLine, Guid Lamp, Guid LampLine)> ConfirmedOrderAsync()
    {
        var orderId = Guid.CreateVersion7();
        var (keyboard, keyboardLine, lamp, lampLine) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await fixture.SeedSucceededPaymentAsync(orderId, grossMinor: 11200, taxMinor: 0);
        await fixture.PublishAsync(new OrderConfirmed(
            orderId, Guid.CreateVersion7(), "buyer@example.com", 11200, "EUR", Ship,
            [
                new OrderLineInfo(keyboard, null, null, "Keyboard", 2, FulfilmentType.Unassigned, BillingMode.OneTime, 5000, 700, LineId: keyboardLine),
                new OrderLineInfo(lamp, null, null, "Lamp", 1, FulfilmentType.Unassigned, BillingMode.OneTime, 1900, 0, LineId: lampLine),
            ],
            PublicOrderNumber: 1));

        await Eventually.UntilAsync(
            async () =>
            {
                using var scope = fixture.Support.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
                return await db.OrderSnapshotLines.CountAsync(l => l.OrderId == orderId && l.OrderLineId != Guid.Empty) == 2;
            },
            () => new TimeoutException($"Support did not project order {orderId} with its line ids."));
        return (orderId, keyboard, keyboardLine, lamp, lampLine);
    }

    private async Task<RefundCompleted> CompletedAsync(Guid refundId)
    {
        await Eventually.UntilAsync(
            () => Task.FromResult(_completed.Any(c => c.RefundId == refundId) && _requested.Any(r => r.RefundId == refundId)),
            () => new TimeoutException($"Refund {refundId} was not observed requested and completed."));
        return _completed.Single(c => c.RefundId == refundId);
    }

    private async Task<Guid> RefundIdAsync(Guid rmaId)
    {
        using var scope = fixture.Support.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
        return (await db.Rmas.AsNoTracking().SingleAsync(r => r.CorrelationId == rmaId)).RefundId;
    }

    private async Task WaitForStateAsync(Guid rmaId, string expected) =>
        await Eventually.UntilAsync(
            async () =>
            {
                using var scope = fixture.Support.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
                return (await db.Rmas.AsNoTracking().FirstOrDefaultAsync(r => r.CorrelationId == rmaId))?.CurrentState == expected;
            },
            () => new TimeoutException($"RMA {rmaId} did not reach {expected}."),
            interval: TimeSpan.FromMilliseconds(300));

    private HttpClient Client<TMarker>(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<TMarker> factory, string role)
        where TMarker : class
    {
        var client = factory.CreateClient();
        // Admin calls here issue refunds, which need payment.refund (refund_permission_enforcement): use the seeded
        // bootstrap admin rather than a bare admin-role claim.
        client.DefaultRequestHeaders.Add(
            InternalClaimsAuth.HeaderName, role == "admin" ? fixture.RefundAdminClaims() : fixture.Claims(role));
        return client;
    }
}
