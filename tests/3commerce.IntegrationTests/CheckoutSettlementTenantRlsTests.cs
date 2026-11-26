using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// settlement_account_tenant (ADR-0039 amendment 2026-11-26), two tenants on one Payments database, connected as a
/// NON-superuser, NOBYPASSRLS owner (the payments_svc posture, so the vault's FORCE RLS policy is enforced) with the REAL
/// migrations. The REAL <see cref="AuthorizePaymentConsumer"/> runs inside MassTransit's EF consumer outbox transaction —
/// as in production — through the REAL keyed <see cref="PaymentProviderRegistry"/> and <see cref="PaymentModeResolver"/>
/// (Sandbox host, Test accounts). Tenant B's checkout settles on tenant B's own storefront account and charges tenant B's
/// member vault + card; tenant A's on tenant A's. Negative controls: the same tenant-B order sent WITHOUT a tenant (an
/// in-flight message from an older Ordering) resolves in the host default tenant A — so it finds neither B's account
/// (synthetic host default instead) nor B's vault (RLS hides it) — and another tenant naming storefront B never reaches
/// B's account.
/// </summary>
[Trait("Category", "Integration")]
public class CheckoutSettlementTenantRlsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").WithCommand("-c", "max_connections=400").Build();
    private static readonly Guid TenantA = Guid.NewGuid(); // the host's configured default tenant
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid StoreA = Guid.CreateVersion7();
    private static readonly Guid StoreB = Guid.CreateVersion7();
    private static readonly Guid UserA = Guid.CreateVersion7();
    private static readonly Guid UserB = Guid.CreateVersion7();
    private readonly Guid _cardB = Guid.CreateVersion7();
    private Guid _accountA;
    private Guid _accountB;
    private string _appConnectionString = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var adminCs = _postgres.GetConnectionString();
        await using (var admin = new NpgsqlConnection(adminCs))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = """
                CREATE ROLE payments_app LOGIN PASSWORD 'app_pw' NOSUPERUSER NOBYPASSRLS;
                GRANT ALL ON SCHEMA public TO payments_app;
                GRANT CREATE ON DATABASE postgres TO payments_app;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        _appConnectionString = new NpgsqlConnectionStringBuilder(adminCs) { Username = "payments_app", Password = "app_pw" }.ConnectionString;

        await using var db = NewContext();
        await db.Database.MigrateAsync(); // the real migrations, as the (non-superuser) owner

        // Each tenant's own storefront acquirer: A settles cards on stripe, B on polar.
        _accountA = AddActiveAccount(db, TenantA, StoreA, "stripe");
        _accountB = AddActiveAccount(db, TenantB, StoreB, "polar");
        await db.SaveChangesAsync();

        // Each tenant's member vault (FORCE RLS): written in its own tenant scope.
        await PaymentsTenantScope.RunAsync(db, TenantA, async () =>
        {
            db.PaymentCustomers.Add(NewCustomer(TenantA, UserA, "cus_tenant_a"));
            await db.SaveChangesAsync();
        }, default);
        await PaymentsTenantScope.RunAsync(db, TenantB, async () =>
        {
            var customer = NewCustomer(TenantB, UserB, "cus_tenant_b");
            db.PaymentCustomers.Add(customer);
            db.SavedPaymentMethods.Add(new SavedPaymentMethod
            {
                Id = _cardB,
                PaymentCustomerId = customer.Id,
                TenantId = TenantB,
                UserId = UserB,
                Provider = "stripe",
                ProviderPaymentMethodId = "pm_tenant_b",
                Brand = "visa",
                Last4 = "4242",
                ExpMonth = 12,
                ExpYear = 2030,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }, default);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Tenant_Bs_checkout_settles_on_tenant_Bs_account_and_charges_tenant_Bs_vault()
    {
        var run = await AuthorizeAsync(Message(TenantB, StoreB, UserB, _cardB));

        var request = Assert.Single(run.Polar.Requests);
        Assert.Equal(_accountB, request.Account.PaymentAccountId);
        Assert.Equal(TenantB, request.Account.TenantId);
        Assert.Equal(("cus_tenant_b", "pm_tenant_b"), (request.ProviderCustomerId, request.ProviderPaymentMethodId));
        Assert.Empty(run.Stripe.Requests);
        Assert.Equal("polar", run.Payment.Provider); // ledger → cash.store.{B}.polar; refunds follow the stored provider
        Assert.Equal(StoreB, run.Payment.StorefrontId);
        Assert.Equal(("cus_tenant_b", "pm_tenant_b"), (run.Payment.ProviderCustomerId, run.Payment.ProviderPaymentMethodId));
        Assert.DoesNotContain(run.Log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Tenant_As_checkout_settles_on_tenant_As_account()
    {
        var run = await AuthorizeAsync(Message(TenantA, StoreA, UserA, savedCard: null));

        var request = Assert.Single(run.Stripe.Requests);
        Assert.Equal(_accountA, request.Account.PaymentAccountId);
        Assert.Equal("cus_tenant_a", request.ProviderCustomerId);
        Assert.Empty(run.Polar.Requests);
        Assert.Equal("stripe", run.Payment.Provider);
    }

    [Fact]
    public async Task Negative_control_the_same_order_without_a_tenant_resolves_in_the_host_default_and_finds_neither_Bs_account_nor_vault()
    {
        var message = Message(tenantId: null, StoreB, UserB, _cardB);

        var run = await AuthorizeAsync(message);

        // No account of tenant A on storefront B → the synthetic host default (stripe); B's vault is invisible in A's scope.
        var request = Assert.Single(run.Stripe.Requests);
        Assert.Equal(Guid.Empty, request.Account.PaymentAccountId);
        Assert.Equal(((string?)null, (string?)null), (request.ProviderCustomerId, request.ProviderPaymentMethodId));
        Assert.Empty(run.Polar.Requests);
        Assert.Equal("stripe", run.Payment.Provider);
        Assert.Contains(run.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(message.OrderId.ToString()));
    }

    [Fact]
    public async Task Negative_control_another_tenant_naming_storefront_B_never_reaches_Bs_account()
    {
        var run = await AuthorizeAsync(Message(TenantA, StoreB, UserA, savedCard: null));

        Assert.Equal(Guid.Empty, Assert.Single(run.Stripe.Requests).Account.PaymentAccountId);
        Assert.Empty(run.Polar.Requests);
    }

    private PaymentsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(_appConnectionString).Options);

    private static Guid AddActiveAccount(PaymentsDbContext db, Guid tenantId, Guid storefrontId, string provider)
    {
        var now = DateTimeOffset.UtcNow;
        var account = PaymentAccount.Create(tenantId, storefrontId, $"{provider} acquirer", provider, PaymentProviderMode.Test, isDefaultForStorefront: true, null, now);
        account.SubmitForApproval(now);
        account.Activate(now);
        db.PaymentAccounts.Add(account);
        return account.Id;
    }

    private static PaymentCustomer NewCustomer(Guid tenantId, Guid userId, string providerCustomerId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        UserId = userId,
        Provider = "stripe",
        ProviderCustomerId = providerCustomerId,
        CreatedAt = StoredTime.Of(DateTimeOffset.UtcNow),
    };

    private static AuthorizePayment Message(Guid? tenantId, Guid storefrontId, Guid userId, Guid? savedCard) =>
        new(Guid.CreateVersion7(), 5_000, "EUR", $"idem-{Guid.NewGuid():N}", UserId: userId, SavedPaymentMethodId: savedCard,
            PaymentOption: "CreditCard", StorefrontId: storefrontId, TenantId: tenantId);

    private sealed record Run(Payment Payment, RailDouble Stripe, RailDouble Polar, LogDouble Log);

    /// <summary>Runs <paramref name="message"/> through the real consumer inside the EF consumer outbox, as production does.</summary>
    private async Task<Run> AuthorizeAsync(AuthorizePayment message)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Mode"] = "Sandbox",
            ["Tenancy:DefaultTenantId"] = TenantA.ToString(),
        }).Build();
        var modes = new PaymentModeResolver(config, new DevEnvironment());
        var stripe = new RailDouble("stripe");
        var polar = new RailDouble("polar");
        var log = new LogDouble();
        await using var services = new ServiceCollection()
            .AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(_appConnectionString))
            .AddSingleton<IConfiguration>(config)
            .AddSingleton(modes)
            .AddSingleton<IPaymentProviderRegistry>(new PaymentProviderRegistry([stripe, polar], modes))
            .AddSingleton(TimeProvider.System)
            .AddLogging()
            .AddSingleton<ILogger<AuthorizePaymentConsumer>>(log)
            .AddMassTransitTestHarness(x =>
            {
                x.AddEntityFrameworkOutbox<PaymentsDbContext>(o =>
                {
                    o.UsePostgres();
                    o.LockStatementProvider = new PostgresLockStatementProvider(enableSchemaCaching: false);
                });
                x.AddConfigureEndpointsCallback((context, _, cfg) => cfg.UseEntityFrameworkOutbox<PaymentsDbContext>(context));
                x.AddConsumer<AuthorizePaymentConsumer>();
            })
            .BuildServiceProvider(true);
        var harness = services.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var response = await harness.GetRequestClient<AuthorizePayment>().GetResponse<AuthorizePaymentResult>(message);
            Assert.False(string.IsNullOrEmpty(response.Message.PaymentIntentId));
        }
        finally
        {
            await harness.Stop();
        }

        await using var db = NewContext();
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == message.OrderId);
        return new Run(payment, stripe, polar, log);
    }

    /// <summary>A keyed rail that records what it was asked to authorize.</summary>
    private sealed class RailDouble(string key) : IPaymentProvider
    {
        public List<PaymentRequest> Requests { get; } = [];
        public string ProviderKey => key;

        public Task<PaymentResponse> AuthorizeAsync(PaymentRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new PaymentResponse($"pi_{key}_{Requests.Count}", "secret", PaymentOutcome.Succeeded, null));
        }

        public Task<string> CreateCustomerAsync(Guid userId, string email, CancellationToken ct) => throw new NotSupportedException();
        public Task<SetupIntentResult> CreateSetupIntentAsync(string providerCustomerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SavedPaymentMethodDetails> GetPaymentMethodAsync(string providerPaymentMethodId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderRefundResult> RefundAsync(string paymentIntentId, long amountMinor, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public PaymentWebhookEvent? ParseWebhook(string payload, string signatureHeader, IReadOnlyList<string> secrets) => null;
    }

    private sealed class LogDouble : ILogger<AuthorizePaymentConsumer>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) { return [.. _entries]; } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries) { _entries.Add((logLevel, formatter(state, exception))); }
        }
    }

    private sealed class DevEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
