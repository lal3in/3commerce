using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Contracts.Tax;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// ADR-0063 P3b: a member's saved default business identity (company name + tax ID) lives on the FORCE-RLS
/// identity."Users" row (ADR-0024), connected as a NON-superuser, NOBYPASSRLS service role — superuser-connected
/// tests would not exercise the policy at all. The owning tenant's scope saves, reads and clears it; another
/// tenant's scope can neither read it nor overwrite it; with no scope a read is empty and an update touches nothing.
/// </summary>
[Trait("Category", "Integration")]
public class IdentityBusinessIdentityRlsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").WithCommand("-c", "max_connections=400").Build();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly BusinessIdentity Acme =
        BusinessIdentityRules.Resolve("Acme GmbH", "DE 123 456 789", TaxIdScheme.EuVat, null);
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

        // EnsureCreated doesn't run migrations — apply the same policy UsersRlsPolicy does (FORCE so the owner is covered).
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE identity."Users" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE identity."Users" FORCE ROW LEVEL SECURITY;
            CREATE POLICY users_tenant_isolation ON identity."Users"
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
                    OR current_setting('app.is_platform_admin', true) = 'true')
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
                    OR current_setting('app.is_platform_admin', true) = 'true');
            """);

        _userId = Guid.CreateVersion7();
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
                Email = $"rls-business-{_userId:N}@example.test",
                PasswordHash = "hash",
                TenantId = TenantA,
            });
            await db.SaveChangesAsync();
        });

        // Saved the way PUT /me/business-identity saves it: load in the member's tenant scope, set, save.
        await using var save = NewContext();
        await save.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA, _userId), async () =>
        {
            var user = await save.Users.SingleAsync(u => u.Id == _userId);
            user.SetBusinessIdentity(Acme);
            await save.SaveChangesAsync();
        });
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private IdentityDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(_appConnectionString).Options);

    private async Task<BusinessIdentity?> ReadAsOwnerAsync()
    {
        await using var db = NewContext();
        var user = await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA),
            () => db.Users.AsNoTracking().SingleAsync(u => u.Id == _userId));
        return user.BusinessIdentity;
    }

    [Fact]
    public async Task Owning_tenant_scope_reads_the_saved_identity_normalized()
    {
        var saved = await ReadAsOwnerAsync();

        Assert.Equal(new BusinessIdentity("Acme GmbH", "DE123456789", TaxIdScheme.EuVat, "DE"), saved);
    }

    [Fact]
    public async Task Other_tenant_scope_cannot_read_the_identity()
    {
        await using var db = NewContext();
        var names = await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantB),
            () => db.Users.AsNoTracking().Where(u => u.Id == _userId).Select(u => u.BusinessCompanyName).ToListAsync());

        Assert.Empty(names);
    }

    [Fact]
    public async Task Other_tenant_scope_cannot_overwrite_or_clear_the_identity()
    {
        await using var db = NewContext();
        var touched = await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantB),
            () => db.Users.Where(u => u.Id == _userId).ExecuteUpdateAsync(set => set
                .SetProperty(u => u.BusinessCompanyName, "Hijack Ltd")
                .SetProperty(u => u.BusinessTaxId, (string?)null)));

        Assert.Equal(0, touched);
        Assert.Equal(Acme, await ReadAsOwnerAsync());
    }

    [Fact]
    public async Task No_scope_read_fails_closed_and_no_scope_update_touches_nothing()
    {
        await using var db = NewContext();
        Assert.Empty(await db.Users.AsNoTracking().Where(u => u.Id == _userId).ToListAsync());

        var touched = await db.Users.Where(u => u.Id == _userId)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.BusinessCompanyName, "No Scope Ltd"));

        Assert.Equal(0, touched);
        Assert.Equal(Acme, await ReadAsOwnerAsync());
    }

    [Fact]
    public async Task Owning_tenant_scope_clears_the_identity()
    {
        await using (var db = NewContext())
        {
            await db.RunInTenantScopeAsync(TenantContext.ForTenant(TenantA, _userId), async () =>
            {
                var user = await db.Users.SingleAsync(u => u.Id == _userId);
                user.SetBusinessIdentity(null);
                await db.SaveChangesAsync();
            });
        }

        Assert.Null(await ReadAsOwnerAsync());
    }
}
