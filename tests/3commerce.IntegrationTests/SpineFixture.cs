using System.Collections.Concurrent;
using MassTransit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using ThreeCommerce.BuildingBlocks.Contracts.Ping;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Shared infra for spine tests: one Postgres (with catalog_db + ordering_db),
/// one RabbitMQ, factory helpers, and a test-side listener collecting every PongResponded.
/// </summary>
public sealed class SpineFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4").Build();
    private IBusControl? _listenerBus;

    public ConcurrentBag<PongResponded> Pongs { get; } = [];

    public string RabbitMqUri { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        await _postgres.ExecScriptAsync("CREATE DATABASE catalog_db;");
        await _postgres.ExecScriptAsync("CREATE DATABASE ordering_db;");
        // Catalog's SearchSchema migration needs pg_trgm (init script does this in real infra).
        await ExecInDbAsync("catalog_db", "CREATE EXTENSION IF NOT EXISTS citext; CREATE EXTENSION IF NOT EXISTS pg_trgm;");

        RabbitMqUri = _rabbitMq.GetConnectionString();

        var pongs = Pongs;
        _listenerBus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            cfg.Host(new Uri(RabbitMqUri));
            cfg.ReceiveEndpoint($"spine-test-listener-{Guid.NewGuid():N}", endpoint =>
            {
                endpoint.Handler<PongResponded>(context =>
                {
                    pongs.Add(context.Message);
                    return Task.CompletedTask;
                });
            });
        });
        await _listenerBus.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_listenerBus is not null)
        {
            await _listenerBus.StopAsync();
        }

        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    public WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> CreateCatalogFactory() =>
        CreateFactory<ThreeCommerce.Catalog.Api.IApiMarker, ThreeCommerce.Catalog.Infrastructure.CatalogDbContext>("catalog_db");

    public WebApplicationFactory<ThreeCommerce.Ordering.Api.IApiMarker> CreateOrderingFactory() =>
        CreateFactory<ThreeCommerce.Ordering.Api.IApiMarker, ThreeCommerce.Ordering.Infrastructure.OrderingDbContext>("ordering_db");

    private WebApplicationFactory<TMarker> CreateFactory<TMarker, TDbContext>(string database)
        where TMarker : class
        where TDbContext : DbContext
    {
        var connectionString = _postgres.GetConnectionString()
            .Replace("Database=postgres", $"Database={database}", StringComparison.Ordinal);

        var factory = new WebApplicationFactory<TMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("ConnectionStrings:RabbitMq", RabbitMqUri);
        });

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TDbContext>().Database.Migrate();

        return factory;
    }

    public async Task<PongResponded> WaitForPongAsync(Guid pingId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = Pongs.FirstOrDefault(p => p.PingId == pingId);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"No PongResponded for {pingId} within {timeout}.");
    }

    private async Task ExecInDbAsync(string database, string sql)
    {
        var result = await _postgres.ExecAsync(["psql", "-U", "postgres", "-d", database, "-c", sql]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Setup SQL failed on {database}: {result.Stderr}");
        }
    }
}
