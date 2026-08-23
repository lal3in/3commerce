using System.Security.Cryptography;
using MassTransit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using ThreeCommerce.BuildingBlocks.Contracts.Catalog;
using ThreeCommerce.BuildingBlocks.Contracts.Entity;
using ThreeCommerce.BuildingBlocks.Contracts.Supply;
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
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18")
        .WithCommand("-c", "max_connections=400").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4").Build();
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly JsonWebTokenHandler _jwt = new();
    private IBusControl? _publishBus;

    public string RabbitMqUri { get; private set; } = string.Empty;
    private string PublicKeyPem => _ecdsa.ExportSubjectPublicKeyInfoPem();

    /// <summary>Mints an internal-claims JWT (as the gateway would) so tests can call admin endpoints.</summary>
    public string MintInternalClaims(Guid userId, string role, string? email = null, bool emailVerified = false, Guid? supplierEntity = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["role"] = role,
            ["sid"] = Guid.NewGuid().ToString(),
            ["tenant"] = "00000000-0000-0000-0000-000000000001",
            ["email_verified"] = emailVerified ? "true" : "false",
        };
        if (!string.IsNullOrWhiteSpace(email))
        {
            claims["email"] = email;
        }

        // Supplier-portal logins carry the supplier entity they are scoped to (self-scope guard).
        if (supplierEntity is { } se)
        {
            claims["supplier_entity"] = se.ToString();
        }

        return _jwt.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "3commerce-gateway",
            Audience = "3commerce-internal",
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_ecdsa), SecurityAlgorithms.EcdsaSha256),
            Claims = claims,
        });
    }

    /// <summary>
    /// Seeds a product whose offer projects as a <see cref="PricingModel.Subscription"/> copy, so a cart line
    /// resolves to <c>BillingMode.Recurring</c> at checkout (drives the verified-member subscription gate).
    /// </summary>
    public async Task<Guid> SeedRecurringProductAsync(long priceMinor, string currency = "EUR", bool approved = true)
    {
        var productId = await SeedProductAsync(priceMinor, currency);
        var supplierId = Guid.CreateVersion7();
        // DECISION A: the supplier must be approved for the offer to count at checkout (approve by default).
        if (approved)
        {
            await ApproveSupplierAsync(supplierId);
        }

        await PublishAsync(new OfferChanged(
            OfferId: Guid.CreateVersion7(),
            TenantId: new Guid("00000000-0000-0000-0000-000000000001"),
            ProductId: productId,
            VariantId: null,
            SupplierId: supplierId,
            SupplyCategory: SupplyCategory.Digital,
            FulfilmentType: FulfilmentType.DigitalDownload,
            PricingModel: PricingModel.Subscription,
            BillingPeriod: BillingPeriod.Monthly,
            Priority: 0,
            Active: true,
            SupplierCostMinor: 0,
            Currency: currency));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await db.OfferCopies.AnyAsync(o => o.ProductId == productId && o.PricingModel == PricingModel.Subscription))
            {
                return productId;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Recurring OfferCopy for product {productId} did not project.");
    }

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
    /// Approval-gated availability (DECISION A): publishes <see cref="SupplierApprovalChanged"/> (as the
    /// Entity service would) and waits for Ordering's SupplierApprovalCopy projection. Checkout only counts
    /// an offer whose supplier is approved, so any seeded offer must have its supplier approved to be
    /// buyable; pass <paramref name="approved"/> false to leave (or revoke) it unapproved.
    /// </summary>
    public async Task ApproveSupplierAsync(Guid supplierId, bool approved = true)
    {
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        await PublishAsync(new SupplierApprovalChanged(tenantId, supplierId, approved));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await db.SupplierApprovalCopies.AnyAsync(s => s.SupplierId == supplierId && s.Approved == approved))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"SupplierApprovalCopy for supplier {supplierId} did not project (approved={approved}).");
    }

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

    /// <summary>
    /// Seeds a guest order (no user) with a given email; returns its id. <paramref name="createdAt"/>
    /// lets a test place the order in the past (e.g. a year before the account is created) to prove the
    /// attach sweep is time-agnostic.
    /// </summary>
    public async Task<Guid> SeedGuestOrderAsync(string email, DateTimeOffset? createdAt = null)
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
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
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

    /// <summary>Whether the Order row has materialized (the saga's owner creates it on confirmation).</summary>
    public async Task<bool> OrderExistsAsync(Guid orderId)
    {
        using var scope = Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        return await db.Orders.AsNoTracking().AnyAsync(o => o.Id == orderId);
    }

    /// <summary>The user recorded for a verified email in Ordering's read copy (FR-7), if any.</summary>
    public async Task<Guid?> VerifiedCustomerUserIdAsync(string email)
    {
        using var scope = Ordering.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        var copy = await db.VerifiedCustomerCopies.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Email == email.ToLowerInvariant());
        return copy?.UserId;
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

    /// <summary>
    /// Seeds a product whose lines will resolve to a supplier + per-unit supplier cost, so a confirmed
    /// order raises OrderCostsRecognized (COGS accrual). Drives the REAL production wiring: the product
    /// read copy is seeded (cart/checkout price against it, and its own SupplierCostMinor is left 0 —
    /// dormant legacy), while the supplier + cost arrive via a published <see cref="OfferChanged"/> that
    /// Ordering's OfferChangedConsumer projects into an OfferCopy — no direct EF write to OfferCopy.
    /// <paramref name="offerCurrency"/> defaults to the order currency; pass a different value to exercise
    /// the no-FX relabel (a foreign-denominated cost carried into the order currency without conversion).
    /// </summary>
    public async Task<(Guid ProductId, Guid SupplierId)> SeedSuppliedProductAsync(
        long priceMinor, long supplierCostMinor, string currency = "EUR", string? offerCurrency = null,
        FulfilmentType fulfilmentType = FulfilmentType.Dropship, bool approved = true)
    {
        var productId = await SeedProductAsync(priceMinor, currency);
        var supplierId = Guid.CreateVersion7();
        var tenantId = new Guid("00000000-0000-0000-0000-000000000001");
        var costCurrency = offerCurrency ?? currency;

        // DECISION A: approve the supplier by default so its offer counts at checkout; a caller wanting to
        // prove the block passes approved:false (the offer projects but the line has no valid supply).
        if (approved)
        {
            await ApproveSupplierAsync(supplierId);
        }

        await PublishAsync(new OfferChanged(
            OfferId: Guid.CreateVersion7(),
            TenantId: tenantId,
            ProductId: productId,
            VariantId: null,
            SupplierId: supplierId,
            SupplyCategory: fulfilmentType.RequiresShipping() ? SupplyCategory.Physical : SupplyCategory.Digital,
            FulfilmentType: fulfilmentType,
            PricingModel: PricingModel.OneTime,
            BillingPeriod: BillingPeriod.Once,
            Priority: 0,
            Active: true,
            SupplierCostMinor: supplierCostMinor,
            Currency: costCurrency));

        // Wait for the projection so checkout resolves the supplier and the accrual can read the cost.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = Ordering.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
            if (await db.OfferCopies.AnyAsync(o => o.ProductId == productId && o.SupplierCostMinor == supplierCostMinor))
            {
                return (productId, supplierId);
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"OfferCopy for product {productId} did not project.");
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
        // Cap each factory's pool so the many per-test factories can't exhaust the shared container's
        // connections ("53300: too many clients" flake); MinPoolSize 0 releases idle connections promptly.
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(
            _postgres.GetConnectionString().Replace("Database=postgres", $"Database={database}", StringComparison.Ordinal))
        {
            MaxPoolSize = 10,
            MinPoolSize = 0,
        }.ConnectionString;

        var factory = new WebApplicationFactory<TMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("ConnectionStrings:RabbitMq", RabbitMqUri);
            builder.UseSetting("InternalAuth:PublicKey", PublicKeyPem);
            builder.UseSetting("Stripe:SecretKey", string.Empty); // force the fake provider
            builder.UseSetting("Scheduling:Enabled", "false");
        });

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TDbContext>().Database.Migrate();
        return factory;
    }
}
