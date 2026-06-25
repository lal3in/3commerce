using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Identity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class IdentityRlsPolicies : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ADR-0024 template, applied first to the only tenant-scoped Identity table that is
        // not yet part of the human login/token path. Users/TenantMemberships/Roles need the
        // next auth-context rewrite before FORCE RLS can be safely enabled there.
        migrationBuilder.Sql("""
            ALTER TABLE identity."ServiceAccounts" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE identity."ServiceAccounts" FORCE ROW LEVEL SECURITY;

            CREATE POLICY service_accounts_tenant_isolation ON identity."ServiceAccounts"
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
            DROP POLICY IF EXISTS service_accounts_tenant_isolation ON identity."ServiceAccounts";
            ALTER TABLE identity."ServiceAccounts" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE identity."ServiceAccounts" DISABLE ROW LEVEL SECURITY;
            """);
    }
}
