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
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").WithCommand("-c", "max_connections=400").Build();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private Guid _entityAId;
    private Guid _onboardingId;
    private Guid _identifierAId;
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

        // EnsureCreated doesn't run migrations — apply the same policies InitialEntitySchema /
        // FixEntityRlsNullifGuard / EntityTenantTablesRls (rev_8) do, for every TenantId table.
        foreach (var table in new[]
                 {
                     "Entities", "EntityRelationships", "DuplicateWarnings", "SupplierOnboardings",
                     "SupplierChangeRequests", "CustomerEntityLinks",
                 })
        {
#pragma warning disable EF1002 // table names come from the compile-time constant array above, not input
            await db.Database.ExecuteSqlRawAsync($"""
                ALTER TABLE entity."{table}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE entity."{table}" FORCE ROW LEVEL SECURITY;
                CREATE POLICY "TenantIsolation_{table}" ON entity."{table}"
                    USING (current_setting('app.is_platform_admin', true) = 'true'
                        OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                        OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
#pragma warning restore EF1002
        }

        // Group B: Entity-aggregate children keyed only by EntityId — isolated via the parent's TenantId
        // (mirrors the EntityChildTablesRls migration's parent-join policy).
        foreach (var table in new[] { "EntityProfiles", "EntityAddresses", "EntityIdentifiers", "EntityContactMethods" })
        {
#pragma warning disable EF1002 // table names come from the compile-time constant array above, not input
            await db.Database.ExecuteSqlRawAsync($"""
                ALTER TABLE entity."{table}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE entity."{table}" FORCE ROW LEVEL SECURITY;
                CREATE POLICY "TenantIsolation_{table}" ON entity."{table}"
                    USING (current_setting('app.is_platform_admin', true) = 'true'
                        OR EXISTS (SELECT 1 FROM entity."Entities" e
                                   WHERE e."Id" = entity."{table}"."EntityId"
                                     AND e."TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid))
                    WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                        OR EXISTS (SELECT 1 FROM entity."Entities" e
                                   WHERE e."Id" = entity."{table}"."EntityId"
                                     AND e."TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid));
                """);
#pragma warning restore EF1002
        }

        var entity = EntityRecord.Create(TenantA, EntityType.Company, "RLS Co", null, DateTimeOffset.UtcNow, []);
        _entityAId = entity.Id;
        // A child keyed only by EntityId (no own TenantId) — exercises the parent-join policy. Uses ACN
        // (not ABN) so it doesn't collide with the ABN the Child_added_... test adds to this same entity.
        var identifier = entity.AddIdentifier(EntityIdentifierType.Acn, "123456789", DateTimeOffset.UtcNow);
        _identifierAId = identifier.Id;
        var onboarding = SupplierOnboarding.Start(entity, DateTimeOffset.UtcNow);
        _onboardingId = onboarding.Id;
        await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA), async () =>
        {
            db.Entities.Add(entity);
            db.SupplierOnboardings.Add(onboarding);
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

    [Fact]
    public async Task Supplier_onboarding_is_tenant_isolated_too()
    {
        await using var db = NewContext();
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => db.SupplierOnboardings.AsNoTracking().AnyAsync(o => o.Id == _onboardingId)));
        Assert.False(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantB),
            () => db.SupplierOnboardings.AsNoTracking().AnyAsync(o => o.Id == _onboardingId)));
    }

    [Fact]
    public async Task No_scope_supplier_onboarding_write_is_rejected()
    {
        await using var db = NewContext();
        var entity = await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => db.Entities.AsNoTracking().FirstAsync(e => e.Id == _entityAId));
        db.SupplierOnboardings.Add(SupplierOnboarding.Start(entity, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Entity_child_keyed_only_by_entity_id_is_tenant_isolated_via_the_parent_join()
    {
        await using var db = NewContext();
        // Visible to the owning tenant (parent Entities row passes the tenant filter)...
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => db.Set<EntityIdentifier>().AsNoTracking().AnyAsync(i => i.Id == _identifierAId)));
        // ...invisible to another tenant (the parent is filtered out, so the child EXISTS-join fails)...
        Assert.False(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantB),
            () => db.Set<EntityIdentifier>().AsNoTracking().AnyAsync(i => i.Id == _identifierAId)));
        // ...and visible under platform scope (bypass), matching the parent policy.
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.Platform(),
            () => db.Set<EntityIdentifier>().AsNoTracking().AnyAsync(i => i.Id == _identifierAId)));
    }

    [Fact]
    public async Task No_scope_read_of_an_entity_child_fails_closed()
    {
        await using var db = NewContext();
        Assert.Empty(await db.Set<EntityIdentifier>().AsNoTracking().Where(i => i.Id == _identifierAId).ToListAsync());
    }

    /// <summary>
    /// Guards the entity sub-resource endpoints (add identifier/contact/address). A child with a
    /// client-generated Guid PK, added through a LOADED navigation, is mis-detected by EF as Modified —
    /// it emits an UPDATE that affects 0 rows (DbUpdateConcurrencyException → HTTP 500), which is why
    /// supplier onboarding always failed with "Supplier is missing: verified ABN or ACN, ...". The fix is
    /// to force the new child to Added so it INSERTs. This proves both the bug and the fix.
    /// </summary>
    [Fact]
    public async Task Child_added_through_loaded_navigation_persists_only_when_forced_added()
    {
        const string value = "51824753556";

        // Without forcing Added: EF UPDATEs a row that was never inserted → concurrency failure.
        await using (var buggy = NewContext())
        {
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                buggy.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA), async () =>
                {
                    var entity = await buggy.Entities.Include(e => e.Identifiers).SingleAsync(e => e.Id == _entityAId);
                    entity.AddIdentifier(EntityIdentifierType.Abn, value, DateTimeOffset.UtcNow);
                    await buggy.SaveChangesAsync();
                    return true;
                }));
        }

        // Forcing Added (the endpoint fix): the identifier INSERTs.
        await using (var corrected = NewContext())
        {
            await corrected.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA), async () =>
            {
                var entity = await corrected.Entities.Include(e => e.Identifiers).SingleAsync(e => e.Id == _entityAId);
                var identifier = entity.AddIdentifier(EntityIdentifierType.Abn, value, DateTimeOffset.UtcNow);
                corrected.Entry(identifier).State = EntityState.Added;
                await corrected.SaveChangesAsync();
                return true;
            });
        }

        await using var verify = NewContext();
        var count = await verify.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => verify.EntityIdentifiers.AsNoTracking().CountAsync(i => i.EntityId == _entityAId && i.Value == value));
        Assert.Equal(1, count);
    }
}
