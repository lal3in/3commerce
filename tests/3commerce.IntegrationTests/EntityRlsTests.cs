using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Entity.Domain;
using ThreeCommerce.Entity.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Proves FORCE ROW LEVEL SECURITY on entity."Entities" isolates rows as a NON-superuser owner
/// (the entity_svc posture). This is the gap that let entity writes 500 in bare-run while the
/// superuser-connected tests passed: nothing exercised RLS as the service role. The fix is the
/// per-request <see cref="TenantScopeMiddleware{T}"/> (BeginTenantScopeAsync); this test exercises the
/// same scope directly — write+read under tenant scope works, another tenant can't see it, and with NO
/// scope BOTH reads fail closed (empty) AND writes are rejected.
/// </summary>
[Trait("Category", "Integration")]
public class EntityRlsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private Guid _entityAId;
    private string _appConnectionString = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var adminCs = _postgres.GetConnectionString();

        // As superuser: a normal owner role (mirrors entity_svc — NOSUPERUSER NOBYPASSRLS so FORCE RLS bites).
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
        await db.Database.EnsureCreatedAsync(); // entity_app owns the schema/tables

        // EnsureCreated doesn't run migrations — apply the same policy InitialEntitySchema does.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE entity."Entities" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE entity."Entities" FORCE ROW LEVEL SECURITY;
            CREATE POLICY "TenantIsolation_Entities" ON entity."Entities"
                USING (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
            """);

        var entity = EntityRecord.Create(TenantA, EntityType.Company, "RLS Co", null, DateTimeOffset.UtcNow, []);
        _entityAId = entity.Id;
        await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA), async () =>
        {
            db.Entities.Add(entity);
            await db.SaveChangesAsync();
        });
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private EntityDbContext NewContext() =>
        new(new DbContextOptionsBuilder<EntityDbContext>().UseNpgsql(_appConnectionString).Options);

    [Fact]
    public async Task Owning_tenant_scope_sees_its_entity()
    {
        await using var db = NewContext();
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => db.Entities.AsNoTracking().AnyAsync(e => e.Id == _entityAId)));
    }

    [Fact]
    public async Task Other_tenant_scope_cannot_see_the_entity()
    {
        await using var db = NewContext();
        Assert.False(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantB),
            () => db.Entities.AsNoTracking().AnyAsync(e => e.Id == _entityAId)));
    }

    [Fact]
    public async Task Platform_scope_sees_the_entity()
    {
        await using var db = NewContext();
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.Platform(),
            () => db.Entities.AsNoTracking().AnyAsync(e => e.Id == _entityAId)));
    }

    [Fact]
    public async Task No_scope_read_fails_closed()
    {
        await using var db = NewContext();
        Assert.Empty(await db.Entities.AsNoTracking().Where(e => e.Id == _entityAId).ToListAsync());
    }

    [Fact]
    public async Task No_scope_write_is_rejected()
    {
        await using var db = NewContext();
        db.Entities.Add(EntityRecord.Create(TenantA, EntityType.Company, "No Scope Co", null, DateTimeOffset.UtcNow, []));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
