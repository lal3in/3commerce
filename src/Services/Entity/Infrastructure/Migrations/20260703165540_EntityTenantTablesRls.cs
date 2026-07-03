using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <summary>
/// Extends FORCE RLS from entity."Entities" (aui_9) to every remaining tenant-scoped entity
/// table (review remediation rev_8 / aui_9 follow-up). Same policy shape as
/// FixEntityRlsNullifGuard: platform scope bypasses; empty app.tenant_id reads as NULL (no
/// 22P02). The per-request TenantScopeMiddleware already sets the context for all writes.
/// </summary>
public partial class EntityTenantTablesRls : Migration
{
    // NB: EntityProfiles has no TenantId (child keyed by EntityId) — its isolation is transitive
    // through the RLS'd Entities parent, so it is deliberately absent here.
    private static readonly string[] Tables =
    [
        "EntityRelationships",
        "DuplicateWarnings",
        "SupplierOnboardings",
        "SupplierChangeRequests",
        "CustomerEntityLinks",
    ];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in Tables)
        {
            migrationBuilder.Sql($"""
                ALTER TABLE entity."{table}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE entity."{table}" FORCE ROW LEVEL SECURITY;
                CREATE POLICY "TenantIsolation_{table}" ON entity."{table}"
                    USING (current_setting('app.is_platform_admin', true) = 'true'
                        OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                        OR "TenantId" = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in Tables)
        {
            migrationBuilder.Sql($"""
                DROP POLICY IF EXISTS "TenantIsolation_{table}" ON entity."{table}";
                ALTER TABLE entity."{table}" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE entity."{table}" DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
