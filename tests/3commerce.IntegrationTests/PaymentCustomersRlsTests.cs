using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// payments."PaymentCustomers" under FORCE RLS (ADR-0024 amendment, owner decision 2026-11-15), connected as a
/// NON-superuser, NOBYPASSRLS owner — the payments_svc posture; a superuser-connected test would not exercise the policy.
/// Applies the REAL migrations, so it proves the PaymentCustomersRls migration itself. Through
/// <see cref="PaymentsTenantScope"/> the owning tenant reads its vault; another tenant reads nothing, updates 0 rows and
/// cannot insert for it; with no scope a read is empty, an insert is rejected and a tracked update fails as a concurrency
/// conflict; platform scope reads. And every reader of the table works under the policy with the tenant its caller holds:
/// <see cref="PaymentCustomerVault"/> (the customer endpoints) finds the vault without calling the provider and saves the
/// learnt email; <see cref="CheckoutInstrument"/> (AuthorizePaymentConsumer, inside its outbox transaction) finds the
/// vault and chosen card in the order's tenant and nothing in another; <see cref="DefaultSavedCard"/> (usage auto top-ups,
/// pay pages) finds the default card by email in its tenant only.
/// </summary>
[Trait("Category", "Integration")]
public class PaymentCustomersRlsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").WithCommand("-c", "max_connections=400").Build();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid UserA = Guid.CreateVersion7();
    private const string EmailA = "ada@rls.example";
    private readonly Guid _customerId = Guid.CreateVersion7();
    private readonly Guid _cardId = Guid.CreateVersion7();
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

        await PaymentsTenantScope.RunAsync(db, TenantA, async () =>
        {
            var customer = NewCustomer(_customerId, TenantA, UserA, "cus_rls_a");
            customer.RecordEmail(EmailA);
            db.PaymentCustomers.Add(customer);
            var card = new SavedPaymentMethod
            {
                Id = _cardId,
                PaymentCustomerId = _customerId,
                TenantId = TenantA,
                UserId = UserA,
                Provider = "stripe",
                ProviderPaymentMethodId = "pm_rls_a",
                Brand = "visa",
                Last4 = "4242",
                ExpMonth = 12,
                ExpYear = 2030,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            card.MakeDefault(DateTimeOffset.UtcNow);
            db.SavedPaymentMethods.Add(card);
            await db.SaveChangesAsync();
        }, default);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private PaymentsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(_appConnectionString).Options);

    private static PaymentCustomer NewCustomer(Guid id, Guid tenantId, Guid userId, string providerCustomerId) => new()
    {
        Id = id,
        TenantId = tenantId,
        UserId = userId,
        Provider = "stripe",
        ProviderCustomerId = providerCustomerId,
        CreatedAt = StoredTime.Of(DateTimeOffset.UtcNow),
    };

    private async Task<PaymentCustomer?> ReadAsAsync(Guid tenantId)
    {
        await using var db = NewContext();
        return await PaymentsTenantScope.RunAsync(db, tenantId,
            () => db.PaymentCustomers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == _customerId), default);
    }

    [Fact]
    public async Task Owning_tenant_scope_reads_the_vault()
    {
        var customer = await ReadAsAsync(TenantA);

        Assert.NotNull(customer);
        Assert.Equal("cus_rls_a", customer!.ProviderCustomerId);
        Assert.Equal(EmailA, customer.Email);
    }

    [Fact]
    public async Task Other_tenant_scope_cannot_read_change_or_insert_for_the_vault()
    {
        Assert.Null(await ReadAsAsync(TenantB));

        await using var db = NewContext();
        var touched = await PaymentsTenantScope.RunAsync(db, TenantB, () => db.PaymentCustomers
            .Where(c => c.Id == _customerId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.ProviderCustomerId, "cus_hijacked")), default);
        Assert.Equal(0, touched);
        Assert.Equal("cus_rls_a", (await ReadAsAsync(TenantA))!.ProviderCustomerId);

        await Assert.ThrowsAsync<DbUpdateException>(() => PaymentsTenantScope.RunAsync(db, TenantB, async () =>
        {
            db.PaymentCustomers.Add(NewCustomer(Guid.CreateVersion7(), TenantA, Guid.CreateVersion7(), "cus_cross"));
            await db.SaveChangesAsync();
        }, default));
    }

    [Fact]
    public async Task No_scope_read_is_empty_insert_is_rejected_and_a_tracked_update_conflicts()
    {
        await using (var db = NewContext())
        {
            Assert.Empty(await db.PaymentCustomers.AsNoTracking().Where(c => c.Id == _customerId).ToListAsync());

            db.PaymentCustomers.Add(NewCustomer(Guid.CreateVersion7(), TenantA, Guid.CreateVersion7(), "cus_unscoped"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // A vault read in scope, then saved after the scope ended: the UPDATE sees no row (0 affected), which EF reports
        // as a concurrency conflict — why PaymentCustomerVault saves the learnt email INSIDE the scope.
        await using (var db = NewContext())
        {
            var customer = await PaymentsTenantScope.RunAsync(db, TenantA,
                () => db.PaymentCustomers.SingleAsync(c => c.Id == _customerId), default);
            customer.RecordEmail("changed@rls.example");
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        }

        Assert.Equal(EmailA, (await ReadAsAsync(TenantA))!.Email);
    }

    [Fact]
    public async Task Platform_scope_reads_the_vault()
    {
        await using var db = NewContext();
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.Platform(),
            () => db.PaymentCustomers.AsNoTracking().AnyAsync(c => c.Id == _customerId)));
    }

    [Fact]
    public async Task Vault_lookup_finds_the_existing_vault_in_the_callers_tenant_and_saves_the_learnt_email()
    {
        await using (var db = NewContext())
        {
            var opened = 0;
            var customer = await PaymentCustomerVault.GetOrCreateAsync(
                db, TenantA, UserA, "stripe", "  Ada.New@RLS.example ",
                _ => { opened++; return Task.FromResult("cus_never"); }, DateTimeOffset.UtcNow, default);

            Assert.Equal(_customerId, customer.Id);
            Assert.Equal(0, opened); // the existing vault — the provider is not asked for a new one
        }

        Assert.Equal("ada.new@rls.example", (await ReadAsAsync(TenantA))!.Email);
    }

    [Fact]
    public async Task Vault_lookup_in_another_tenant_opens_that_tenants_own_vault()
    {
        await using var db = NewContext();
        var userB = Guid.CreateVersion7();
        var customer = await PaymentCustomerVault.GetOrCreateAsync(
            db, TenantB, userB, "stripe", "bea@rls.example",
            _ => Task.FromResult("cus_rls_b"), DateTimeOffset.UtcNow, default);

        Assert.Equal(TenantB, customer.TenantId);
        await using var reader = NewContext();
        var stored = await PaymentsTenantScope.RunAsync(reader, TenantB,
            () => reader.PaymentCustomers.AsNoTracking().SingleAsync(c => c.UserId == userB), default);
        Assert.Equal(("cus_rls_b", "bea@rls.example"), (stored.ProviderCustomerId, stored.Email));
        Assert.False(await PaymentsTenantScope.RunAsync(reader, TenantA,
            () => reader.PaymentCustomers.AsNoTracking().AnyAsync(c => c.UserId == userB), default));
    }

    [Fact]
    public async Task Checkout_instrument_is_read_in_the_orders_tenant_inside_the_open_outbox_transaction()
    {
        // AuthorizePaymentConsumer runs inside MassTransit's EF outbox transaction; the scope applies to it.
        await using var db = NewContext();
        await using var outer = await db.Database.BeginTransactionAsync();

        var mine = await CheckoutInstrument.FindAsync(db, TenantA, UserA, _cardId, default);
        var elsewhere = await CheckoutInstrument.FindAsync(db, TenantB, UserA, _cardId, default);

        Assert.Equal(new CheckoutInstrument("cus_rls_a", "pm_rls_a"), mine);
        Assert.Equal(new CheckoutInstrument(null, null), elsewhere); // not this tenant's vault: no customer, no card
        Assert.Same(outer, db.Database.CurrentTransaction);
        await outer.CommitAsync();

        Assert.Equal(new CheckoutInstrument("cus_rls_a", null),
            await CheckoutInstrument.FindAsync(db, TenantA, UserA, Guid.CreateVersion7(), default)); // an unknown card
    }

    [Fact]
    public async Task Default_card_by_email_is_found_in_its_tenant_only()
    {
        await using var db = NewContext();

        Assert.Equal(new DefaultSavedCard("cus_rls_a", "pm_rls_a"), await DefaultSavedCard.FindAsync(db, TenantA, EmailA, default));
        Assert.Null(await DefaultSavedCard.FindAsync(db, TenantB, EmailA, default));
        Assert.Equal(new DefaultSavedCard("cus_rls_a", "pm_rls_a"), await DefaultSavedCard.FindOwnedAsync(db, TenantA, UserA, _cardId, default));
        Assert.Null(await DefaultSavedCard.FindOwnedAsync(db, TenantB, UserA, _cardId, default));
    }
}
