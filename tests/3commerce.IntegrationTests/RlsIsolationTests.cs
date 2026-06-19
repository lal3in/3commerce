using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using ThreeCommerce.BuildingBlocks.Infrastructure.Tenancy;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Proves the RLS pattern of ADR-0024 against a real Postgres: a transaction-local tenant
/// context isolates rows, fails closed with no context, and does not leak across scopes on a
/// reused connection. Uses FORCE ROW LEVEL SECURITY because the service connects as table owner.
/// </summary>
public class RlsIsolationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private RlsTestContext _db = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // The container's default user is a superuser, which BYPASSES RLS. Create a normal
        // login role that OWNS the table (mirrors production: e.g. identity_svc owns identity_db
        // but is not a superuser) so RLS — with FORCE, since the owner is otherwise exempt —
        // actually applies.
        var adminCs = _postgres.GetConnectionString();
        await using (var admin = new RlsTestContext(adminCs))
        {
            await admin.Database.ExecuteSqlRawAsync("""
                CREATE ROLE app_user LOGIN PASSWORD 'app_pw' NOSUPERUSER NOBYPASSRLS;
                GRANT ALL ON SCHEMA public TO app_user;
                """);
        }

        var appCs = new NpgsqlConnectionStringBuilder(adminCs) { Username = "app_user", Password = "app_pw" }.ConnectionString;
        _db = new RlsTestContext(appCs);
        await _db.Database.EnsureCreatedAsync();

        // Seed rows BEFORE enabling RLS.
        _db.Widgets.AddRange(
            new Widget { Id = Guid.NewGuid(), TenantId = TenantA, Name = "a1" },
            new Widget { Id = Guid.NewGuid(), TenantId = TenantB, Name = "b1" });
        await _db.SaveChangesAsync();

        await _db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "Widgets" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "Widgets" FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON "Widgets"
                USING ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
                       OR current_setting('app.is_platform_admin', true) = 'true')
                WITH CHECK ("TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
                       OR current_setting('app.is_platform_admin', true) = 'true');
            """);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task No_context_sees_no_rows_fail_closed()
    {
        var rows = await _db.Widgets.AsNoTracking().ToListAsync();
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Tenant_scope_sees_only_its_own_rows()
    {
        var rows = await _db.RunInTenantScopeAsync(
            TenantContext.ForTenant(TenantA), () => _db.Widgets.AsNoTracking().ToListAsync());

        Assert.Single(rows);
        Assert.Equal(TenantA, rows[0].TenantId);
    }

    [Fact]
    public async Task Context_does_not_leak_after_the_scope_commits()
    {
        await _db.RunInTenantScopeAsync(
            TenantContext.ForTenant(TenantA), () => _db.Widgets.AsNoTracking().ToListAsync());

        // SET LOCAL was rolled back at commit -> the reused connection fails closed again.
        Assert.Empty(await _db.Widgets.AsNoTracking().ToListAsync());

        var bRows = await _db.RunInTenantScopeAsync(
            TenantContext.ForTenant(TenantB), () => _db.Widgets.AsNoTracking().ToListAsync());
        Assert.Single(bRows);
        Assert.Equal(TenantB, bRows[0].TenantId);
    }

    [Fact]
    public async Task Platform_admin_scope_sees_all_tenants()
    {
        var rows = await _db.RunInTenantScopeAsync(
            TenantContext.Platform(), () => _db.Widgets.AsNoTracking().ToListAsync());

        Assert.Equal(2, rows.Count);
    }

    private sealed class Widget
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public required string Name { get; set; }
    }

    private sealed class RlsTestContext(string connectionString) : DbContext
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseNpgsql(connectionString);
    }
}
