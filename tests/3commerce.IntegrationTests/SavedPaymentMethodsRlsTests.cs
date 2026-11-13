using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// payments."SavedPaymentMethods" under FORCE RLS (ADR-0024, owner decision 2026-11-07, mem_2), connected as a
/// NON-superuser, NOBYPASSRLS owner — the payments_svc posture; a superuser-connected test would not exercise the
/// policy at all. Applies the REAL migrations (<c>MigrateAsync</c>, like <see cref="SellerEstablishmentRlsTests"/>), so it
/// proves the SavedPaymentMethodsRls migration itself. Through <see cref="SavedCardTenantScope"/> — what every reader
/// and writer uses — the owning tenant writes, reads and updates its card (billing address included); another tenant
/// sees nothing and changes nothing; with no scope a read is empty and an insert is rejected; platform scope reads; and
/// inside an already-open transaction (a consumer in MassTransit's EF outbox transaction) the scope applies to it.
/// </summary>
[Trait("Category", "Integration")]
public class SavedPaymentMethodsRlsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").WithCommand("-c", "max_connections=400").Build();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid UserA = Guid.CreateVersion7();
    private Guid _customerId;
    private Guid _cardId;
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

        _customerId = Guid.CreateVersion7();
        db.PaymentCustomers.Add(new PaymentCustomer
        {
            Id = _customerId,
            TenantId = TenantA,
            UserId = UserA,
            Provider = "stripe",
            ProviderCustomerId = "cus_rls",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(); // PaymentCustomers is not RLS'd

        _cardId = Guid.CreateVersion7();
        await SavedCardTenantScope.RunAsync(db, TenantA, async () =>
        {
            var card = NewCard(_cardId, TenantA);
            card.SetBillingAddress(CardBillingAddress.Create("Ada Buyer", "1 Policy St", "Melbourne", "VIC", "3000", "AU"), DateTimeOffset.UtcNow);
            db.SavedPaymentMethods.Add(card);
            await db.SaveChangesAsync();
        }, default);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private PaymentsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(_appConnectionString).Options);

    private SavedPaymentMethod NewCard(Guid id, Guid tenantId) => new()
    {
        Id = id,
        PaymentCustomerId = _customerId,
        TenantId = tenantId,
        UserId = UserA,
        Provider = "stripe",
        ProviderPaymentMethodId = $"pm_card_visa_{id:N}",
        Brand = "visa",
        Last4 = "4242",
        ExpMonth = 12,
        ExpYear = 2030,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task<SavedPaymentMethod?> ReadAsAsync(Guid tenantId)
    {
        await using var db = NewContext();
        return await SavedCardTenantScope.RunAsync(db, tenantId,
            () => db.SavedPaymentMethods.AsNoTracking().SingleOrDefaultAsync(m => m.Id == _cardId), default);
    }

    [Fact]
    public async Task Owning_tenant_scope_reads_the_card_and_its_billing_address()
    {
        var card = await ReadAsAsync(TenantA);

        Assert.NotNull(card);
        Assert.Equal("Melbourne", card!.BillingAddress?.City);
    }

    [Fact]
    public async Task Other_tenant_scope_cannot_read_or_change_the_card()
    {
        Assert.Null(await ReadAsAsync(TenantB));

        await using var db = NewContext();
        var touched = await SavedCardTenantScope.RunAsync(db, TenantB, () => db.SavedPaymentMethods
            .Where(m => m.Id == _cardId)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.BillingCity, "Hijacked")), default);

        Assert.Equal(0, touched);
        Assert.Equal("Melbourne", (await ReadAsAsync(TenantA))!.BillingAddress?.City);
    }

    [Fact]
    public async Task Other_tenant_scope_cannot_write_a_card_for_another_tenant()
    {
        await using var db = NewContext();
        await Assert.ThrowsAsync<DbUpdateException>(() => SavedCardTenantScope.RunAsync(db, TenantB, async () =>
        {
            db.SavedPaymentMethods.Add(NewCard(Guid.CreateVersion7(), TenantA));
            await db.SaveChangesAsync();
        }, default));
    }

    [Fact]
    public async Task No_scope_read_fails_closed_and_no_scope_insert_is_rejected()
    {
        await using var db = NewContext();
        Assert.Empty(await db.SavedPaymentMethods.AsNoTracking().Where(m => m.Id == _cardId).ToListAsync());

        db.SavedPaymentMethods.Add(NewCard(Guid.CreateVersion7(), TenantA));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Platform_scope_reads_the_card()
    {
        await using var db = NewContext();
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.Platform(),
            () => db.SavedPaymentMethods.AsNoTracking().AnyAsync(m => m.Id == _cardId)));
    }

    [Fact]
    public async Task Inside_an_open_transaction_the_scope_applies_to_it()
    {
        // A consumer runs inside MassTransit's EF outbox transaction; the scope must not open a second one.
        await using var db = NewContext();
        await using var outer = await db.Database.BeginTransactionAsync();

        var card = await SavedCardTenantScope.RunAsync(db, TenantA,
            () => db.SavedPaymentMethods.AsNoTracking().SingleOrDefaultAsync(m => m.Id == _cardId), default);

        Assert.NotNull(card);
        Assert.Same(outer, db.Database.CurrentTransaction);
        await outer.CommitAsync();

        // The setting was transaction-local: after the commit the same connection sees nothing again.
        Assert.Empty(await db.SavedPaymentMethods.AsNoTracking().Where(m => m.Id == _cardId).ToListAsync());
    }
}
