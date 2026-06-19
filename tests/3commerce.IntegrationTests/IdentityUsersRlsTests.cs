using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Domain.Tenancy;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Proves FORCE ROW LEVEL SECURITY on identity."Users" (ADR-0024, UsersRlsPolicy migration)
/// actually isolates rows as a NON-superuser owner — the production posture (identity_svc) that
/// neither the superuser-connected IdentityAuthTests nor the anonymous CI smoke jobs exercise.
/// It uses the real IdentityDbContext + the same BeginTenantScopeAsync/RunInTenantScopeAsync the
/// AuthService relies on: tenant scope to write/read a user, platform scope for the cross-tenant
/// read that introspection performs, and fail-closed with no scope.
/// </summary>
[Trait("Category", "Integration")]
public class IdentityUsersRlsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid UserAId = Guid.NewGuid();
    private static readonly Guid AddressAId = Guid.NewGuid();
    private string _appConnectionString = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var adminCs = _postgres.GetConnectionString();

        // As superuser: enable citext + create a normal owner role (mirrors identity_svc).
        await using (var admin = new NpgsqlConnection(adminCs))
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = """
                CREATE EXTENSION IF NOT EXISTS citext;
                CREATE ROLE identity_app LOGIN PASSWORD 'app_pw' NOSUPERUSER NOBYPASSRLS;
                GRANT ALL ON SCHEMA public TO identity_app;
                GRANT CREATE ON DATABASE postgres TO identity_app;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        _appConnectionString = new NpgsqlConnectionStringBuilder(adminCs) { Username = "identity_app", Password = "app_pw" }.ConnectionString;

        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync(); // identity_app owns the schema/tables

        // EnsureCreated doesn't run migrations, so apply the same policy the UsersRlsPolicy migration does.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE identity."Users" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE identity."Users" FORCE ROW LEVEL SECURITY;
            CREATE POLICY users_tenant_isolation ON identity."Users"
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
                       OR current_setting('app.is_platform_admin', true) = 'true')
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
                       OR current_setting('app.is_platform_admin', true) = 'true');
            ALTER TABLE identity."Addresses" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE identity."Addresses" FORCE ROW LEVEL SECURITY;
            CREATE POLICY addresses_tenant_isolation ON identity."Addresses"
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
                       OR current_setting('app.is_platform_admin', true) = 'true')
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
                       OR current_setting('app.is_platform_admin', true) = 'true');
            """);

        // Tenants/Principals are not RLS'd — seed the FK targets directly.
        var now = DateTimeOffset.UtcNow;
        db.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "A", Slug = $"a-{TenantA:N}", HomeRegion = "dev", Status = TenantStatus.Active, CreatedAt = now },
            new Tenant { Id = TenantB, Name = "B", Slug = $"b-{TenantB:N}", HomeRegion = "dev", Status = TenantStatus.Active, CreatedAt = now });
        var principal = new Principal { Id = Guid.NewGuid(), Type = PrincipalType.Human, CreatedAt = now };
        db.Principals.Add(principal);
        await db.SaveChangesAsync();

        // Write a user in tenant A under tenant scope (the AuthService register path).
        await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA), async () =>
        {
            db.Users.Add(new User
            {
                Id = UserAId,
                TenantId = TenantA,
                PrincipalId = principal.Id,
                Email = $"a-{UserAId:N}@example.test",
                PasswordHash = "x",
                CreatedAt = now,
            });
            db.Addresses.Add(new Address
            {
                Id = AddressAId,
                UserId = UserAId,
                TenantId = TenantA,
                Name = "A",
                Line1 = "1 St",
                City = "Berlin",
                Postcode = "10115",
                Country = "DE",
            });
            await db.SaveChangesAsync();
        });
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private IdentityDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(_appConnectionString).Options);

    [Fact]
    public async Task Owning_tenant_scope_sees_its_user()
    {
        await using var db = NewContext();
        var found = await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => db.Users.AsNoTracking().AnyAsync(u => u.Id == UserAId));
        Assert.True(found);
    }

    [Fact]
    public async Task Other_tenant_scope_cannot_see_the_user()
    {
        await using var db = NewContext();
        var found = await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantB),
            () => db.Users.AsNoTracking().AnyAsync(u => u.Id == UserAId));
        Assert.False(found);
    }

    [Fact]
    public async Task Platform_scope_sees_the_user_like_introspection()
    {
        await using var db = NewContext();
        var found = await db.RunInTenantScopeAsync(TenantContext.Platform(),
            () => db.Users.AsNoTracking().AnyAsync(u => u.Id == UserAId));
        Assert.True(found);
    }

    [Fact]
    public async Task No_scope_fails_closed()
    {
        await using var db = NewContext();
        Assert.Empty(await db.Users.AsNoTracking().Where(u => u.Id == UserAId).ToListAsync());
    }

    [Fact]
    public async Task Address_owning_tenant_scope_sees_it_other_does_not()
    {
        await using var db = NewContext();
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => db.Addresses.AsNoTracking().AnyAsync(a => a.Id == AddressAId)));
        Assert.False(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantB),
            () => db.Addresses.AsNoTracking().AnyAsync(a => a.Id == AddressAId)));
    }

    [Fact]
    public async Task Address_no_scope_fails_closed()
    {
        await using var db = NewContext();
        Assert.Empty(await db.Addresses.AsNoTracking().Where(a => a.Id == AddressAId).ToListAsync());
    }
}
