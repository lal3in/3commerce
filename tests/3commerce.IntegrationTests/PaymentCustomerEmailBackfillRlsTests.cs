using MassTransit;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Contracts.Payments;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Tools.OrderConfirmedBackfill;
using ThreeCommerce.Tools.OrderConfirmedBackfill.PaymentCustomers;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// payment_customer_email_backfill under FORCE RLS on payments."PaymentCustomers" + "SavedPaymentMethods" (ADR-0024),
/// connected as a NON-superuser, NOBYPASSRLS owner with the REAL migrations — the payments_svc posture a superuser test
/// would not exercise. <see cref="PaymentCustomerEmailBackfill"/> (what <c>PaymentCustomerEmailConsumer</c> runs) works in
/// the command's tenant scope: it fills a vault with no email, leaves a known email alone, announces
/// <see cref="CustomerPaymentMethodReady"/> when the member has a default card (after which the auto top-up finds that card
/// by email), works inside an already-open (outbox) transaction, and cannot reach another tenant's vault. The backfill
/// tool's <see cref="PaymentsVaultSource"/> enumerates tenants in platform scope and reads each tenant's vaults in its scope.
/// </summary>
[Trait("Category", "Integration")]
public class PaymentCustomerEmailBackfillRlsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").WithCommand("-c", "max_connections=400").Build();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
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
        await db.Database.MigrateAsync(); // the real migrations (incl. PaymentCustomersRls), as the non-superuser owner
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task A_vault_without_an_email_learns_it_and_its_default_card_is_announced_and_found()
    {
        var user = Guid.CreateVersion7();
        var vault = await SeedVaultAsync(TenantA, user, email: null, defaultCard: "pm_backfill_a");
        var published = new CapturingPublisher();

        await using (var db = NewContext())
        {
            Assert.Null(await DefaultSavedCard.FindAsync(db, TenantA, "ada@backfill.example", default)); // hidden before
            var outcome = await Backfill(db, published).RecordAsync(new RecordPaymentCustomerEmail(TenantA, user, " Ada@Backfill.example "), default);
            Assert.Equal(PaymentCustomerEmailOutcome.RecordedAndReady, outcome);
        }

        var ready = Assert.IsType<CustomerPaymentMethodReady>(Assert.Single(published.Published));
        Assert.Equal((TenantA, "ada@backfill.example"), (ready.TenantId, ready.CustomerEmail));
        Assert.Equal("ada@backfill.example", (await ReadVaultAsync(TenantA, vault))!.Email);
        await using var reader = NewContext();
        Assert.Equal("pm_backfill_a", (await DefaultSavedCard.FindAsync(reader, TenantA, "ada@backfill.example", default))?.ProviderPaymentMethodId);
    }

    [Fact]
    public async Task A_known_email_is_left_alone_and_nothing_is_announced()
    {
        var user = Guid.CreateVersion7();
        var vault = await SeedVaultAsync(TenantA, user, email: "current@backfill.example", defaultCard: "pm_known_a");
        var published = new CapturingPublisher();

        await using (var db = NewContext())
        {
            var outcome = await Backfill(db, published).RecordAsync(new RecordPaymentCustomerEmail(TenantA, user, "stale@backfill.example"), default);
            Assert.Equal(PaymentCustomerEmailOutcome.AlreadyKnown, outcome);
        }

        Assert.Equal("current@backfill.example", (await ReadVaultAsync(TenantA, vault))!.Email);
        Assert.Empty(published.Published);
    }

    [Fact]
    public async Task Inside_an_open_outbox_transaction_the_email_is_recorded_without_a_card_and_a_redelivery_is_a_no_op()
    {
        // PaymentCustomerEmailConsumer runs inside MassTransit's EF outbox transaction; the tenant scope applies to it.
        var user = Guid.CreateVersion7();
        var vault = await SeedVaultAsync(TenantA, user, email: null, defaultCard: null);
        var published = new CapturingPublisher();
        var command = new RecordPaymentCustomerEmail(TenantA, user, "bea@backfill.example");

        await using (var db = NewContext())
        {
            await using var outbox = await db.Database.BeginTransactionAsync();
            Assert.Equal(PaymentCustomerEmailOutcome.Recorded, await Backfill(db, published).RecordAsync(command, default));
            Assert.Same(outbox, db.Database.CurrentTransaction);
            await outbox.CommitAsync();
        }

        await using (var db = NewContext())
        {
            Assert.Equal(PaymentCustomerEmailOutcome.AlreadyKnown, await Backfill(db, published).RecordAsync(command, default));
        }

        Assert.Equal("bea@backfill.example", (await ReadVaultAsync(TenantA, vault))!.Email);
        Assert.Empty(published.Published); // no default card: nothing to announce
    }

    [Fact]
    public async Task A_command_for_another_tenant_cannot_reach_the_vault()
    {
        var user = Guid.CreateVersion7();
        var vault = await SeedVaultAsync(TenantA, user, email: null, defaultCard: "pm_cross_a");
        var published = new CapturingPublisher();

        await using (var db = NewContext())
        {
            var outcome = await Backfill(db, published).RecordAsync(new RecordPaymentCustomerEmail(TenantB, user, "eve@backfill.example"), default);
            Assert.Equal(PaymentCustomerEmailOutcome.NoVault, outcome);
        }

        Assert.Null((await ReadVaultAsync(TenantA, vault))!.Email);
        Assert.Empty(published.Published);
    }

    [Fact]
    public async Task The_tools_vault_source_lists_tenants_in_platform_scope_and_vaults_in_each_tenants_scope()
    {
        var userA = Guid.CreateVersion7();
        var userB = Guid.CreateVersion7();
        var vaultA = await SeedVaultAsync(TenantA, userA, email: null, defaultCard: null);
        var vaultB = await SeedVaultAsync(TenantB, userB, email: "kept@backfill.example", defaultCard: null);

        await using var db = NewContext();
        var source = new PaymentsVaultSource(db, new RlsTenantScope());
        var tenants = await source.TenantsAsync(default);
        Assert.Contains(TenantA, tenants);
        Assert.Contains(TenantB, tenants);

        var inA = await source.VaultsAsync(TenantA, default);
        Assert.Contains(inA, v => v.VaultId == vaultA && v.Email is null);
        Assert.DoesNotContain(inA, v => v.VaultId == vaultB);
        Assert.All(inA, v => Assert.Equal(TenantA, v.TenantId));
        Assert.Contains(await source.VaultsAsync(TenantB, default), v => v.VaultId == vaultB && v.Email == "kept@backfill.example");
    }

    private PaymentsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(_appConnectionString).Options);

    private static PaymentCustomerEmailBackfill Backfill(PaymentsDbContext db, CapturingPublisher published) =>
        new(db, published, TimeProvider.System);

    private async Task<Guid> SeedVaultAsync(Guid tenantId, Guid userId, string? email, string? defaultCard)
    {
        var now = StoredTime.Of(DateTimeOffset.UtcNow);
        var vault = new PaymentCustomer
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = userId,
            Provider = "stripe",
            ProviderCustomerId = $"cus_{userId:N}",
            CreatedAt = now,
        };
        if (email is not null)
        {
            vault.RecordEmail(email);
        }

        await using var db = NewContext();
        await PaymentsTenantScope.RunAsync(db, tenantId, async () =>
        {
            db.PaymentCustomers.Add(vault);
            if (defaultCard is not null)
            {
                var card = new SavedPaymentMethod
                {
                    Id = Guid.CreateVersion7(),
                    PaymentCustomerId = vault.Id,
                    TenantId = tenantId,
                    UserId = userId,
                    Provider = "stripe",
                    ProviderPaymentMethodId = defaultCard,
                    Brand = "visa",
                    Last4 = "4242",
                    ExpMonth = 12,
                    ExpYear = 2030,
                    CreatedAt = now,
                };
                card.MakeDefault(now);
                db.SavedPaymentMethods.Add(card);
            }

            await db.SaveChangesAsync();
        }, default);
        return vault.Id;
    }

    private async Task<PaymentCustomer?> ReadVaultAsync(Guid tenantId, Guid vaultId)
    {
        await using var db = NewContext();
        return await PaymentsTenantScope.RunAsync(db, tenantId,
            () => db.PaymentCustomers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == vaultId), default);
    }

    private sealed class CapturingPublisher : IPublishEndpoint
    {
        public readonly List<object> Published = [];

        public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class
        {
            Published.Add(message);
            return Task.CompletedTask;
        }

        public Task Publish<T>(T message, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class =>
            Publish(message, cancellationToken);

        public Task Publish<T>(T message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class =>
            Publish(message, cancellationToken);

        public Task Publish(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish(object message, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish(object message, Type messageType, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();

        public Task Publish<T>(object values, IPipe<PublishContext<T>> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();

        public Task Publish<T>(object values, IPipe<PublishContext> publishPipe, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();

        public ConnectHandle ConnectPublishObserver(IPublishObserver observer) => throw new NotSupportedException();
    }
}
