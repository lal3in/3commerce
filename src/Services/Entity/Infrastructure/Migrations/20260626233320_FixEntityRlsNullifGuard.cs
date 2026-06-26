using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixEntityRlsNullifGuard : Migration
    {
        // Make the tenant-isolation policy robust: an empty/unset app.tenant_id (e.g. a MasterGlobal/
        // platform scope, which sets tenant_id='') must read as NULL, not error on ''::uuid. Mirrors the
        // Identity UsersRlsPolicy. The TenantScopeMiddleware (per-request scope) is what actually sets the
        // context; this just stops the policy from throwing 22P02 on the empty-string edge.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
            DROP POLICY IF EXISTS "TenantIsolation_Entities" ON entity."Entities";
            CREATE POLICY "TenantIsolation_Entities" ON entity."Entities"
                USING (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
            """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
            DROP POLICY IF EXISTS "TenantIsolation_Entities" ON entity."Entities";
            CREATE POLICY "TenantIsolation_Entities" ON entity."Entities"
                USING (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = current_setting('app.tenant_id', true)::uuid)
                WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                    OR "TenantId" = current_setting('app.tenant_id', true)::uuid);
            """);
    }
}
