using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Entity.Domain;
using ThreeCommerce.Entity.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0063 P2 / ADR-0024: entity."SellerEstablishments" is FORCE-RLS isolated for a NON-superuser owner
/// (the entity_svc posture). Unlike <see cref="EntityRlsTests"/> this applies the REAL migrations
/// (<c>MigrateAsync</c> as the owner role), so it proves the SellerIdentity migration's own policy — not a
/// hand-copied one: the owning tenant writes and reads, another tenant sees nothing and cannot write under the
/// victim's entity, no scope fails closed, and platform scope sees all.
/// </summary>
[Trait("Category", "Integration")]
public class SellerEstablishmentRlsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").WithCommand("-c", "max_connections=400").Build();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private Guid _sellerAId;
    private Guid _establishmentAId;
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
                CREATE ROLE entity_app LOGIN PASSWORD 'app_pw' NOSUPERUSER NOBYPASSRLS;
                GRANT ALL ON SCHEMA public TO entity_app;
                GRANT CREATE ON DATABASE postgres TO entity_app;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        _appConnectionString = new NpgsqlConnectionStringBuilder(adminCs) { Username = "entity_app", Password = "app_pw" }.ConnectionString;

        await using var db = NewContext();
        await db.Database.MigrateAsync(); // the real migrations, as the (non-superuser) owner

        var seller = EntityRecord.Create(TenantA, EntityType.Company, "Seller A Pty Ltd", null, Now, []);
        seller.DesignateAsSeller(Now);
        var establishment = seller.AddEstablishment("sydney-hq", Details("AU"), Now);
        _sellerAId = seller.Id;
        _establishmentAId = establishment.Id;
        await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA), async () =>
        {
            db.Entities.Add(seller);
            await db.SaveChangesAsync();
        });
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private static SellerEstablishmentDetails Details(string country) =>
        new("HQ", "1 George St", null, "Sydney", "NSW", "2000", country, null);

    private EntityDbContext NewContext() =>
        new(new DbContextOptionsBuilder<EntityDbContext>()
            .UseNpgsql(_appConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public"))
            .Options);

    [Fact]
    public async Task Policy_is_forced_on_the_table()
    {
        await using var db = NewContext();
        var flags = await db.Database.SqlQueryRaw<bool>("""
            SELECT c.relforcerowsecurity AS "Value" FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'entity' AND c.relname = 'SellerEstablishments'
            """).ToListAsync();
        Assert.True(Assert.Single(flags));
    }

    [Fact]
    public async Task Owning_tenant_sees_its_establishment_and_another_tenant_does_not()
    {
        await using var db = NewContext();
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => db.SellerEstablishments.AsNoTracking().AnyAsync(e => e.Id == _establishmentAId)));
        Assert.False(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantB),
            () => db.SellerEstablishments.AsNoTracking().AnyAsync(e => e.Id == _establishmentAId)));
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.Platform(),
            () => db.SellerEstablishments.AsNoTracking().AnyAsync(e => e.Id == _establishmentAId)));
    }

    [Fact]
    public async Task No_scope_read_fails_closed()
    {
        await using var db = NewContext();
        Assert.Empty(await db.SellerEstablishments.AsNoTracking().Where(e => e.Id == _establishmentAId).ToListAsync());
    }

    [Fact]
    public async Task Another_tenant_cannot_write_an_establishment_under_the_victims_entity()
    {
        // Load tenant A's seller under A's scope, then try to add an establishment while scoped as tenant B:
        // the parent-join WITH CHECK rejects it (42501), so a tenant can never plant rows in another's entity.
        await using var reader = NewContext();
        var seller = await reader.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => reader.Entities.AsNoTracking().Include(e => e.Profiles).Include(e => e.Establishments).SingleAsync(e => e.Id == _sellerAId));
        var planted = seller.AddEstablishment("planted", Details("AU"), Now);

        await using var attacker = NewContext();
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => attacker.RunInTenantScopeAsync(TenantContext.ForTenant(TenantB), async () =>
        {
            attacker.SellerEstablishments.Add(planted);
            await attacker.SaveChangesAsync();
            return true;
        }));

        // Positive control: the same insert under the owning tenant succeeds.
        await using var owner = NewContext();
        var other = seller.AddEstablishment("melbourne", Details("AU"), Now);
        await owner.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA), async () =>
        {
            owner.SellerEstablishments.Add(other);
            await owner.SaveChangesAsync();
            return true;
        });
        await using var verify = NewContext();
        var keys = await verify.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => verify.SellerEstablishments.AsNoTracking().Where(e => e.EntityId == _sellerAId).Select(e => e.Key).ToListAsync());
        Assert.Equal(["melbourne", "sydney-hq"], keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task No_scope_write_is_rejected()
    {
        await using var reader = NewContext();
        var seller = await reader.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => reader.Entities.AsNoTracking().Include(e => e.Profiles).Include(e => e.Establishments).SingleAsync(e => e.Id == _sellerAId));
        var unscoped = seller.AddEstablishment("unscoped", Details("AU"), Now);

        await using var db = NewContext();
        db.SellerEstablishments.Add(unscoped);
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
