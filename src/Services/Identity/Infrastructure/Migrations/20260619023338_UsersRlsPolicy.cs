using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Identity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class UsersRlsPolicy : Migration
{
    // Tenant-isolate the Users table (ADR-0024). FORCE because the service connects as the
    // table owner (identity_svc), who would otherwise bypass RLS. Sessions/EmailTokens stay
    // secret-keyed (global TokenHash lookups, no TenantId) and are not row-isolated here.
    // Auth flows set the context via BeginTenantScopeAsync (tenant scope for register/login;
    // platform scope for the cross-tenant, secret-keyed introspection/verify/reset paths).
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE identity."Users" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE identity."Users" FORCE ROW LEVEL SECURITY;

            CREATE POLICY users_tenant_isolation ON identity."Users"
                USING (
                    "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
                    OR current_setting('app.is_platform_admin', true) = 'true'
                )
                WITH CHECK (
                    "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid
                    OR current_setting('app.is_platform_admin', true) = 'true'
                );
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS users_tenant_isolation ON identity."Users";
            ALTER TABLE identity."Users" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE identity."Users" DISABLE ROW LEVEL SECURITY;
            """);
    }
}
