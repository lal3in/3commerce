using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// identity."Addresses" FORCE RLS as a NON-superuser service role (review rev_8). The policy has
/// shipped since AddressTenantScope (20260619) but the mt1_4-promised "dedicated address policy
/// test" never landed — this is it (the tracker note claiming FORCE RLS was still deferred was stale).
/// Mirrors EntityRlsTests: owning scope reads+writes, other tenant blind, platform scope reads,
/// no scope fails closed for reads and rejects writes.
/// </summary>
[Trait("Category", "Integration")]
public class IdentityAddressesRlsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private Guid _addressId;
    private Guid _userId;
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

        // EnsureCreated doesn't run migrations — apply the same policies UsersRlsPolicy and
        // AddressTenantScope do (FORCE so the owning role is covered too).
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

        _userId = Guid.CreateVersion7();
        _addressId = Guid.CreateVersion7();
        await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA), async () =>
        {
            db.Tenants.Add(new ThreeCommerce.Identity.Domain.Tenancy.Tenant
            {
                Id = TenantA,
                Name = "RLS Tenant",
                Slug = $"rls-{TenantA:N}",
                HomeRegion = "dev",
                Status = ThreeCommerce.Identity.Domain.Tenancy.TenantStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Users.Add(new User
            {
                Id = _userId,
                Email = $"rls-{_userId:N}@example.test",
                PasswordHash = "hash",
                TenantId = TenantA,
            });
            db.Addresses.Add(new Address
            {
                Id = _addressId,
                UserId = _userId,
                TenantId = TenantA,
                Name = "RLS Home",
                Line1 = "1 Policy St",
                City = "Melbourne",
                Postcode = "3000",
                Country = "AU",
            });
            await db.SaveChangesAsync();
        });
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private IdentityDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(_appConnectionString).Options);

    [Fact]
    public async Task Owning_tenant_scope_sees_its_address()
    {
        await using var db = NewContext();
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => db.Addresses.AsNoTracking().AnyAsync(a => a.Id == _addressId)));
    }

    [Fact]
    public async Task Other_tenant_scope_cannot_see_the_address()
    {
        await using var db = NewContext();
        Assert.False(await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantB),
            () => db.Addresses.AsNoTracking().AnyAsync(a => a.Id == _addressId)));
    }

    [Fact]
    public async Task Platform_scope_sees_the_address()
    {
        await using var db = NewContext();
        Assert.True(await db.RunInTenantScopeAsync(TenantContext.Platform(),
            () => db.Addresses.AsNoTracking().AnyAsync(a => a.Id == _addressId)));
    }

    [Fact]
    public async Task No_scope_read_fails_closed()
    {
        await using var db = NewContext();
        Assert.Empty(await db.Addresses.AsNoTracking().Where(a => a.Id == _addressId).ToListAsync());
    }

    [Fact]
    public async Task No_scope_write_is_rejected()
    {
        await using var db = NewContext();
        db.Addresses.Add(new Address
        {
            Id = Guid.CreateVersion7(),
            UserId = _userId,
            TenantId = TenantA,
            Name = "No Scope",
            Line1 = "2 Policy St",
            City = "Melbourne",
            Postcode = "3000",
            Country = "AU",
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
