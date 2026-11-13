using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <summary>
/// payments."SavedPaymentMethods" under FORCE ROW LEVEL SECURITY (ADR-0024, owner decision 2026-11-07, mem_2): each
/// row carries a member's card reference and, since mem_2, its billing address, so it is tenant-isolated by the
/// database as well as by the owner checks. Same policy shape as Entity's (EntityTenantTablesRls): platform scope
/// bypasses; an empty app.tenant_id reads as NULL (no 22P02) and so matches no row. FORCE because payments_svc owns the
/// table (a non-superuser, non-BYPASSRLS role — infra/postgres/init-databases.sql). Every reader and writer runs in a
/// tenant scope through <c>SavedCardTenantScope</c>.
/// </summary>
public partial class SavedPaymentMethodsRls : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE payments."SavedPaymentMethods" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE payments."SavedPaymentMethods" FORCE ROW LEVEL SECURITY;
            CREATE POLICY "TenantIsolation_SavedPaymentMethods" ON payments."SavedPaymentMethods"
                USING (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS "TenantIsolation_SavedPaymentMethods" ON payments."SavedPaymentMethods";
            ALTER TABLE payments."SavedPaymentMethods" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE payments."SavedPaymentMethods" DISABLE ROW LEVEL SECURITY;
            """);
    }
}
