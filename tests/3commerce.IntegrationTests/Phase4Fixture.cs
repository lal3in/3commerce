using System.Security.Cryptography;
using MassTransit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Support.Domain;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Operations infra: Support, Payments and Fulfillment hosts on a shared RabbitMQ so the
/// RMA saga can drive the Phase-3 refund path end to end. Fake payment provider (no keys).
/// </summary>
public sealed class Phase4Fixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4").Build();
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly JsonWebTokenHandler _jwt = new();
    private IBusControl? _publishBus;

    public string RabbitMqUri { get; private set; } = string.Empty;
    private string PublicKeyPem => _ecdsa.ExportSubjectPublicKeyInfoPem();

    public WebApplicationFactory<ThreeCommerce.Support.Api.IApiMarker> Support { get; private set; } = null!;
    public WebApplicationFactory<ThreeCommerce.Payments.Api.IApiMarker> Payments { get; private set; } = null!;
    public WebApplicationFactory<ThreeCommerce.Fulfillment.Api.IApiMarker> Fulfillment { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
        RabbitMqUri = _rabbitMq.GetConnectionString();
        await _postgres.ExecScriptAsync("CREATE DATABASE support_db;");
        await _postgres.ExecScriptAsync("CREATE DATABASE payments_db;");
        await _postgres.ExecScriptAsync("CREATE DATABASE fulfillment_db;");

        Support = CreateFactory<ThreeCommerce.Support.Api.IApiMarker, ThreeCommerce.Support.Infrastructure.SupportDbContext>("support_db");
        Payments = CreateFactory<ThreeCommerce.Payments.Api.IApiMarker, PaymentsDbContext>("payments_db");
        Fulfillment = CreateFactory<ThreeCommerce.Fulfillment.Api.IApiMarker, ThreeCommerce.Fulfillment.Infrastructure.FulfillmentDbContext>("fulfillment_db");

        // Direct publisher (bypasses the EF bus outbox, which only delivers on SaveChanges).
        _publishBus = Bus.Factory.CreateUsingRabbitMq(cfg => cfg.Host(new Uri(RabbitMqUri)));
        await _publishBus.StartAsync();
    }

    /// <summary>Publishes straight to the broker (no outbox), for tests standing in for another service.</summary>
    public Task PublishAsync<T>(T message) where T : class => _publishBus!.Publish(message);

    public async Task DisposeAsync()
    {
        if (_publishBus is not null)
        {
            await _publishBus.StopAsync();
        }

        await Support.DisposeAsync();
        await Payments.DisposeAsync();
        await Fulfillment.DisposeAsync();
        _ecdsa.Dispose();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    public string Claims(string role) => _jwt.CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "3commerce-gateway",
        Audience = "3commerce-internal",
        IssuedAt = DateTime.UtcNow,
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_ecdsa), SecurityAlgorithms.EcdsaSha256),
        Claims = new Dictionary<string, object> { ["sub"] = Guid.CreateVersion7().ToString(), ["role"] = role },
    });

    /// <summary>Seeds a captured payment so the refund path has something to reverse.</summary>
    public async Task SeedSucceededPaymentAsync(Guid orderId, long grossMinor, long taxMinor)
    {
        using var scope = Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        db.Payments.Add(new Payment
        {
            Id = Guid.CreateVersion7(),
            OrderId = orderId,
            PaymentIntentId = $"pi_seed_{orderId:N}",
            AmountMinor = grossMinor,
            TaxMinor = taxMinor,
            Currency = "EUR",
            Status = PaymentStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds the Support order read-copy (BL-8) directly, the way OrderSnapshotConsumer would
    /// from OrderConfirmed. The RMA endpoint derives the refund amount from this snapshot.
    /// With no explicit lines, a single line priced at the gross is added.
    /// </summary>
    public async Task SeedOrderSnapshotAsync(
        Guid orderId, long grossMinor, string email = "buyer@example.com",
        params (Guid ProductId, string Title, long UnitPriceMinor, int Quantity)[] lines)
    {
        using var scope = Support.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SupportDbContext>();
        if (await db.OrderSnapshots.AnyAsync(o => o.OrderId == orderId))
        {
            return;
        }

        var effective = lines.Length > 0
            ? lines
            : [(Guid.CreateVersion7(), "Item", grossMinor, 1)];

        db.OrderSnapshots.Add(new OrderSnapshot
        {
            OrderId = orderId,
            Email = email,
            GrossMinor = grossMinor,
            Currency = "EUR",
            Lines = effective.Select(l => new OrderSnapshotLine
            {
                Id = Guid.CreateVersion7(),
                OrderId = orderId,
                ProductId = l.ProductId,
                Title = l.Title,
                UnitPriceMinor = l.UnitPriceMinor,
                Quantity = l.Quantity,
            }).ToList(),
        });
        await db.SaveChangesAsync();
    }

    public async Task<long> PaymentsTrialBalanceAsync()
    {
        using var scope = Payments.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        return await db.JournalLines.SumAsync(l => l.DebitMinor) - await db.JournalLines.SumAsync(l => l.CreditMinor);
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
            builder.UseSetting("Stripe:SecretKey", string.Empty);
            builder.UseSetting("Tax:FlatRate", "0.19");
        });

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TDbContext>().Database.Migrate();
        return factory;
    }
}
