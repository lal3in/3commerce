using System.Net;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ThreeCommerce.BuildingBlocks.Contracts.Identity;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Writes to one <c>ordering."Orders"</c> row from OUTSIDE the order's event partition
/// (order_number_and_edit_races_2026_10_11):
/// <list type="bullet">
/// <item>the supplier/operator <c>mark-delivered</c> HTTP endpoint, now guarded by the row's <c>xmin</c> as an EF
/// concurrency token: a stale client Version, or a commit between the endpoint's read and its save, answers
/// 409 problem+json (<c>orderRefusal</c> 4) instead of overwriting the newer state;</item>
/// <item>the FR-7 guest attach, which the EmailVerified sweep used to apply itself and now fans out as one
/// <c>GuestOrderOwnerVerified</c> per order, applied on the order's partition with its status events.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Collection(Phase3Collection.Name)]
public sealed class OrderEditConcurrencyTests(Phase3Fixture fixture) : IAsyncLifetime
{
    private const int ChangedConcurrently = 4; // OrderRefusal.ChangedConcurrently

    private static readonly string[] ErrorQueues = ["order-status_error", "guest-order-attach_error"];

    private static readonly string[] RaceSignatures = ["23505", "40001", "could not serialize"];

    private ConsumerRaceProbe _probe = null!;

    public async Task InitializeAsync() =>
        _probe = await ConsumerRaceProbe.StartAsync(fixture.Ordering.Services, fixture.RabbitMqUri, ErrorQueues, RaceSignatures);

    public async Task DisposeAsync() => await _probe.DisposeAsync();

    [Fact]
    public async Task Mark_delivered_with_the_current_version_succeeds_and_is_idempotent()
    {
        var orderId = await SeedConfirmedOrderAsync();
        using var operatorClient = OperatorClient();
        var before = await DetailAsync(operatorClient, orderId);

        var marked = await operatorClient.PostAsJsonAsync(MarkDelivered(orderId), new { version = before.Version });

        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        var status = (await marked.Content.ReadFromJsonAsync<StatusDto>())!;
        Assert.Equal("Delivered", status.Status);
        Assert.NotEqual(before.Version, status.Version); // the update moved xmin
        Assert.Equal(status.Version, (await DetailAsync(operatorClient, orderId)).Version);

        // Already delivered: 200 again, even with the now-stale version (the requested end state holds).
        var again = await operatorClient.PostAsJsonAsync(MarkDelivered(orderId), new { version = before.Version });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // A body-less call (older clients) still works — the concurrency token is enforced at save either way.
        var other = await SeedConfirmedOrderAsync();
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.PostAsync(MarkDelivered(other), null)).StatusCode);
    }

    [Fact]
    public async Task Mark_delivered_with_a_stale_version_is_a_409_problem_and_changes_nothing()
    {
        var orderId = await SeedConfirmedOrderAsync();
        using var operatorClient = OperatorClient();
        var loaded = await DetailAsync(operatorClient, orderId);

        // The order changes after the operator loaded it: a dispute opens.
        await PublishAndWaitAsync(new PaymentDisputed(orderId, $"pi_fake_{orderId:N}", 1190));
        Assert.True((await RowAsync(orderId)).Disputed);

        var stale = await operatorClient.PostAsJsonAsync(MarkDelivered(orderId), new { version = loaded.Version });
        var problem = await ProblemAssert.OrderRefusedAsync(stale, ChangedConcurrently, "This order was just updated — refresh and try again.");
        var current = problem.GetProperty("currentVersion").GetUInt32();
        Assert.NotEqual(loaded.Version, current);

        var row = await RowAsync(orderId);
        Assert.Equal(OrderStatus.Confirmed, row.Status);
        Assert.True(row.Disputed);

        // After a refresh (the version the 409 reported) the same action succeeds.
        var retried = await operatorClient.PostAsJsonAsync(MarkDelivered(orderId), new { version = current });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(OrderStatus.Delivered, (await RowAsync(orderId)).Status);
        await _probe.AssertNoRaceAsync();
    }

    [Fact]
    public async Task A_commit_between_the_read_and_the_save_is_a_409_and_the_other_write_survives()
    {
        // Deterministic interleaving: another transaction (standing in for a status consumer) holds the row
        // locked with an update while mark-delivered runs. The endpoint reads the old committed row — version
        // matches — and its UPDATE … AND xmin = @loaded blocks on the lock; once the other write commits, the row's
        // xmin has moved, the UPDATE matches nothing, and EF raises DbUpdateConcurrencyException → 409.
        var orderId = await SeedConfirmedOrderAsync();
        using var operatorClient = OperatorClient();
        var loaded = await DetailAsync(operatorClient, orderId);

        await using var other = new NpgsqlConnection(ConnectionString());
        await other.OpenAsync();
        await using var tx = await other.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand(
            """UPDATE ordering."Orders" SET "Disputed" = true WHERE "Id" = @id""", other, tx))
        {
            update.Parameters.AddWithValue("id", orderId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var request = operatorClient.PostAsJsonAsync(MarkDelivered(orderId), new { version = loaded.Version });
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(request.IsCompleted, "mark-delivered should be waiting on the row lock");
        await tx.CommitAsync();

        await ProblemAssert.OrderRefusedAsync(await request, ChangedConcurrently);
        var row = await RowAsync(orderId);
        Assert.Equal(OrderStatus.Confirmed, row.Status); // the HTTP write rolled back whole (no OrderDelivered either)
        Assert.True(row.Disputed);                       // and did not overwrite the concurrent one
    }

    [Fact]
    public async Task Concurrent_status_event_and_mark_delivered_one_wins_cleanly_with_no_lost_update()
    {
        // Real consumers this time: PaymentDisputed and mark-delivered fired together for many orders. Whichever
        // commits second either retries (the consumer: 40001 under REPEATABLE READ → retried on a fresh snapshot)
        // or answers 409 (the endpoint). Either way the dispute flag is never lost, and the status is Delivered
        // exactly when the endpoint said 200.
        const int Orders = 12;
        var orders = new List<Guid>();
        for (var i = 0; i < Orders; i++)
        {
            orders.Add(await SeedConfirmedOrderAsync());
        }

        using var operatorClient = OperatorClient();
        var bus = fixture.Ordering.Services.GetRequiredService<IBus>();
        var messageIds = orders.Select(_ => NewId.NextGuid()).ToList();
        var responses = await Task.WhenAll(orders.Select(async (orderId, i) =>
        {
            var publish = bus.Publish(new PaymentDisputed(orderId, $"pi_fake_{orderId:N}", 1190), c => c.MessageId = messageIds[i]);
            var post = operatorClient.PostAsync(MarkDelivered(orderId), null);
            await publish;
            return await post;
        }));
        await _probe.WaitForReceivedAsync(messageIds, "every dispute processed by Ordering");

        for (var i = 0; i < Orders; i++)
        {
            var row = await RowAsync(orders[i]);
            Assert.True(row.Disputed, $"order {orders[i]} lost its Disputed flag");
            if (responses[i].StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal(OrderStatus.Delivered, row.Status);
            }
            else
            {
                await ProblemAssert.OrderRefusedAsync(responses[i], ChangedConcurrently);
                Assert.Equal(OrderStatus.Confirmed, row.Status);
            }
        }

        await _probe.AssertErrorQueuesUnchangedAsync(); // the consumer side may retry by design, but never faults
    }

    [Fact]
    public async Task Guest_attach_concurrent_with_status_events_applies_both_without_serialization_failures()
    {
        const int Rounds = 3;
        const int OrdersPerEmail = 6;
        var bus = fixture.Ordering.Services.GetRequiredService<IBus>();
        for (var round = 0; round < Rounds; round++)
        {
            var email = $"attach-race-{round}-{Guid.NewGuid():N}@example.com";
            var userId = Guid.CreateVersion7();
            var orders = new List<Guid>();
            for (var i = 0; i < OrdersPerEmail; i++)
            {
                orders.Add(await fixture.SeedGuestOrderAsync(email));
            }

            // The shopper verifies their email while each guest order is being disputed and partially refunded.
            var messageIds = new List<Guid>();
            var publishes = new List<Task> { bus.Publish(new EmailVerified(userId, email)) };
            foreach (var orderId in orders)
            {
                object[] events =
                [
                    new PaymentDisputed(orderId, $"pi_fake_{orderId:N}", 1190),
                    new RefundCompleted(Guid.CreateVersion7(), orderId, 300),
                ];
                foreach (var message in events)
                {
                    var id = NewId.NextGuid();
                    messageIds.Add(id);
                    publishes.Add(bus.Publish(message, message.GetType(), c => c.MessageId = id));
                }
            }

            await Task.WhenAll(publishes);
            await _probe.WaitForReceivedAsync(messageIds, "every status event processed by Ordering");
            await ConsumerRaceProbe.WaitUntilAsync(
                async () => (await RowsAsync(orders)).Values.All(o => o.UserId == userId), "every guest order attached");

            foreach (var row in (await RowsAsync(orders)).Values)
            {
                Assert.Equal(userId, row.UserId);
                Assert.True(row.Disputed, $"order {row.Id} lost its Disputed flag");
                Assert.True(row.PartiallyRefunded, $"order {row.Id} lost its PartiallyRefunded flag");
            }
        }

        await _probe.AssertNoRaceAsync();
    }

    private static string MarkDelivered(Guid orderId) => $"/orders/supplier/{orderId}/mark-delivered";

    private HttpClient OperatorClient()
    {
        var client = fixture.Ordering.CreateClient();
        client.DefaultRequestHeaders.Add(InternalClaimsAuth.HeaderName, fixture.MintInternalClaims(Guid.CreateVersion7(), "admin"));
        return client;
    }

    private static async Task<DetailDto> DetailAsync(HttpClient client, Guid orderId) =>
        (await client.GetFromJsonAsync<DetailDto>($"/admin/orders/{orderId}"))!;

    private async Task PublishAndWaitAsync<T>(T message) where T : class
    {
        var id = NewId.NextGuid();
        await fixture.Ordering.Services.GetRequiredService<IBus>().Publish(message, c => c.MessageId = id);
        await _probe.WaitForReceivedAsync([id], $"{typeof(T).Name} processed");
    }

    private async Task<Guid> SeedConfirmedOrderAsync()
    {
        var id = Guid.CreateVersion7();
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.Orders.Add(new Order
        {
            Id = id,
            TenantId = new Guid("00000000-0000-0000-0000-000000000001"),
            Email = $"edit-{id:N}@example.com",
            Status = OrderStatus.Confirmed,
            NetMinor = 1000,
            TaxMinor = 190,
            GrossMinor = 1190,
            Currency = "EUR",
            ShipName = "Edit",
            ShipLine1 = "1 St",
            ShipCity = "Berlin",
            ShipPostcode = "10115",
            ShipCountry = "DE",
            CreatedAt = DateTimeOffset.UtcNow,
            Lines =
            [
                new OrderLine
                {
                    Id = Guid.CreateVersion7(),
                    OrderId = id,
                    ProductId = Guid.CreateVersion7(),
                    Title = "Edit widget",
                    UnitPriceMinor = 1000,
                    Quantity = 1,
                    SupplierId = Guid.CreateVersion7(),
                },
            ],
        });
        await db.SaveChangesAsync();
        return id;
    }

    private string ConnectionString()
    {
        using var scope = fixture.Ordering.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<OrderingDbContext>().Database.GetConnectionString()!;
    }

    private async Task<Order> RowAsync(Guid orderId) => (await RowsAsync([orderId]))[orderId];

    private async Task<Dictionary<Guid, Order>> RowsAsync(IEnumerable<Guid> orderIds)
    {
        var ids = orderIds.ToList();
        using var scope = fixture.Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.Orders.AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
    }

    private sealed record DetailDto(Guid Id, string Status, bool Disputed, uint Version);

    private sealed record StatusDto(Guid Id, string Status, uint? Version);
}
