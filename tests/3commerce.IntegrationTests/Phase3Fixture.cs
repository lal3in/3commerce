using System.Security.Cryptography;
using MassTransit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using ThreeCommerce.Ordering.Domain;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Money-flow infra: one Postgres (ordering_db + payments_db), one RabbitMQ shared by the
/// Ordering and Payments hosts so the cross-service checkout request/response and saga work.
/// No Stripe keys → Payments uses the deterministic FakePaymentProvider.
/// </summary>
public sealed class Phase3Fixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4").Build();
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private IBusControl? _publishBus;

    public string RabbitMqUri { get; private set; } = string.Empty;
    private string PublicKeyPem => _ecdsa.ExportSubjectPublicKeyInfoPem();

    public WebApplicationFactory<ThreeCommerce.Ordering.Api.IApiMarker> Ordering { get; private set; } = null!;
    public WebApplicationFactory<ThreeCommerce.Payments.Api.IApiMarker> Payments { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
        RabbitMqUri = _rabbitMq.GetConnectionString();
        await _postgres.ExecScriptAsync("CREATE DATABASE ordering_db;");
        await _postgres.ExecScriptAsync("CREATE DATABASE payments_db;");

        Ordering = CreateFactory<ThreeCommerce.Ordering.Api.IApiMarker, OrderingDbContext>("ordering_db");
        Payments = CreateFactory<ThreeCommerce.Payments.Api.IApiMarker, PaymentsDbContext>("payments_db");

        _publishBus = Bus.Factory.CreateUsingRabbitMq(cfg => cfg.Host(new Uri(RabbitMqUri)));
        await _publishBus.StartAsync();
    }

    /// <summary>Publishes straight to the broker (no outbox), standing in for another service.</summary>
    public Task PublishAsync<T>(T message) where T : class => _publishBus!.Publish(message);

    /// <summary>
    /// Chaos hook (NFR-2): tears down and re-creates the Ordering host — the saga's owner —
    /// to simulate an outage. Its durable queues survive on the broker, so messages published
    /// while it is down are delivered on restart. Safe because the collection runs serially.
    /// </summary>
    public async Task RestartOrderingAsync()
    {
        await Ordering.DisposeAsync();
        Ordering = CreateFactory<ThreeCommerce.Ordering.Api.IApiMarker, OrderingDbContext>("ordering_db");
        // Force the host to build so its bus/consumers reconnect before we poll it.
        _ = Ordering.Services;
    }

    /// <summary>Seeds a guest order (no user) with a given email; returns its id.</summary>
    public async Task<Guid> SeedGuestOrderAsync(string email)
    {
        var id = Guid.CreateVersion7();
        using var scope = Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.Orders.Add(new Order
        {
            Id = id,
            UserId = null,
            Email = email,
            Status = OrderStatus.Confirmed,
            NetMinor = 1000,
            TaxMinor = 190,
            GrossMinor = 1190,
            Currency = "EUR",
            ShipName = "Guest",
            ShipLine1 = "1 St",
            ShipCity = "Berlin",
            ShipPostcode = "10115",
            ShipCountry = "DE",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    public async Task<Guid?> OrderUserIdAsync(Guid orderId)
    {
        using var scope = Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).UserId;
    }

    public async Task DisposeAsync()
    {
        if (_publishBus is not null)
        {
            await _publishBus.StopAsync();
        }

        await Ordering.DisposeAsync();
        await Payments.DisposeAsync();
        _ecdsa.Dispose();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    /// <summary>Seeds a product into Ordering's local read copy (stands in for a Catalog event).</summary>
    public async Task<Guid> SeedProductAsync(long priceMinor, string currency = "EUR")
    {
        var id = Guid.CreateVersion7();
        using var scope = Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.ProductCopies.Add(new ProductCopy
        {
            ProductId = id,
            Slug = $"p-{id:N}",
            Title = "Test Product",
            MinPriceMinor = priceMinor,
            Currency = currency,
            ImageUrl = null,
        });
        await db.SaveChangesAsync();
        return id;
    }

    public async Task<long> TrialBalanceAsync()
    {
        using var scope = Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var debits = await db.JournalLines.SumAsync(l => l.DebitMinor);
        var credits = await db.JournalLines.SumAsync(l => l.CreditMinor);
        return debits - credits;
    }

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
            builder.UseSetting("InternalAuth:PublicKey", PublicKeyPem);
            builder.UseSetting("Stripe:SecretKey", string.Empty); // force the fake provider
            builder.UseSetting("Tax:FlatRate", "0.19");
        });

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TDbContext>().Database.Migrate();
        return factory;
    }
}
