using System.Net.Http.Json;
using MassTransit;
using ThreeCommerce.BuildingBlocks.Contracts.Ping;

namespace ThreeCommerce.IntegrationTests;

[Trait("Category", "Integration")]
public class SpineTests(SpineFixture fixture) : IClassFixture<SpineFixture>
{
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(30);

    private sealed record PingResponseDto(Guid PingId);

    [Fact]
    public async Task Ping_flows_to_pong_across_services()
    {
        using var catalog = fixture.CreateCatalogFactory();
        using var ordering = fixture.CreateOrderingFactory();
        using var client = catalog.CreateClient();

        var response = await client.PostAsync("/ping", content: null);
        response.EnsureSuccessStatusCode();
        var ping = await response.Content.ReadFromJsonAsync<PingResponseDto>();

        var pong = await fixture.WaitForPongAsync(ping!.PingId, PongTimeout);
        Assert.Equal("ordering", pong.RespondedBy);
    }

    [Fact]
    public async Task Outbox_message_survives_consumer_downtime()
    {
        // Start Ordering once so its durable queue exists, then take it down.
        var ordering = fixture.CreateOrderingFactory();
        ordering.Dispose();

        using var catalog = fixture.CreateCatalogFactory();
        using var client = catalog.CreateClient();

        var response = await client.PostAsync("/ping", content: null);
        response.EnsureSuccessStatusCode();
        var ping = await response.Content.ReadFromJsonAsync<PingResponseDto>();

        // Consumer is down; the message waits durably. Bring Ordering back and it must arrive.
        using var orderingRestarted = fixture.CreateOrderingFactory();
        var pong = await fixture.WaitForPongAsync(ping!.PingId, PongTimeout);
        Assert.Equal(ping.PingId, pong.PingId);
    }

    [Fact]
    public async Task Duplicate_delivery_produces_single_pong()
    {
        using var ordering = fixture.CreateOrderingFactory();

        var pingId = Guid.CreateVersion7();
        var messageId = Guid.NewGuid();
        var message = new PingRequested(pingId, DateTimeOffset.UtcNow);

        // Same MessageId twice: the EF inbox on Ordering's endpoint must dedup.
        var bus = Bus.Factory.CreateUsingRabbitMq(cfg => cfg.Host(new Uri(fixture.RabbitMqUri)));
        await bus.StartAsync();
        try
        {
            await bus.Publish(message, ctx => ctx.MessageId = messageId);
            await bus.Publish(message, ctx => ctx.MessageId = messageId);
        }
        finally
        {
            await bus.StopAsync();
        }

        await fixture.WaitForPongAsync(pingId, PongTimeout);
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Single(fixture.Pongs, p => p.PingId == pingId);
    }
}
