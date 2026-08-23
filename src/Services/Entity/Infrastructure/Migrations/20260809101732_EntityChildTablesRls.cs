using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <summary>
/// Extends tenant-isolation Row-Level Security from entity."Entities" to its child tables (the caveat
/// follow-up). Defense-in-depth: the app already scopes every query via TenantScopeMiddleware
/// (app.tenant_id / app.is_platform_admin GUCs), but RLS makes a leak impossible even if a query
/// forgets to scope. Two policy shapes:
///   • own-TenantId tables (mirror the Entities policy);
///   • Entity-aggregate children keyed only by "EntityId" — isolated by joining to the parent's TenantId.
/// Platform scope (app.is_platform_admin = 'true') bypasses, matching Entities.
/// </summary>
public partial class EntityChildTablesRls : Migration
{
    // Tables that carry their own TenantId column — same policy as entity."Entities".
    private static readonly string[] OwnTenantTables =
    {
        "EntityRelationships", "DuplicateWarnings", "SupplierOnboardings", "SupplierChangeRequests", "AuditEntries",
    };

    // Entity-aggregate children keyed only by EntityId — isolate via the parent Entities row's TenantId.
    private static readonly string[] EntityChildTables =
    {
        "EntityProfiles", "EntityAddresses", "EntityIdentifiers", "EntityContactMethods",
    };

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in OwnTenantTables)
        {
            // DROP … IF EXISTS first: EntityTenantTablesRls (20260703165540) already created
            // TenantIsolation_* on four of these five tables, so a plain CREATE POLICY fails with
            // 42710 on a fresh database (the whole migrate step exits 1). Dropping first makes this
            // migration idempotent; the recreated policy is byte-for-byte identical.
            migrationBuilder.Sql($@"
                    ALTER TABLE entity.""{table}"" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE entity.""{table}"" FORCE ROW LEVEL SECURITY;
                    DROP POLICY IF EXISTS ""TenantIsolation_{table}"" ON entity.""{table}"";
                    CREATE POLICY ""TenantIsolation_{table}"" ON entity.""{table}""
                        USING (current_setting('app.is_platform_admin', true) = 'true'
                            OR ""TenantId"" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                            OR ""TenantId"" = nullif(current_setting('app.tenant_id', true), '')::uuid);");
        }

        foreach (var table in EntityChildTables)
        {
            migrationBuilder.Sql($@"
                    ALTER TABLE entity.""{table}"" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE entity.""{table}"" FORCE ROW LEVEL SECURITY;
                    DROP POLICY IF EXISTS ""TenantIsolation_{table}"" ON entity.""{table}"";
                    CREATE POLICY ""TenantIsolation_{table}"" ON entity.""{table}""
                        USING (current_setting('app.is_platform_admin', true) = 'true'
                            OR EXISTS (SELECT 1 FROM entity.""Entities"" e
                                       WHERE e.""Id"" = entity.""{table}"".""EntityId""
                                         AND e.""TenantId"" = nullif(current_setting('app.tenant_id', true), '')::uuid))
                        WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                            OR EXISTS (SELECT 1 FROM entity.""Entities"" e
                                       WHERE e.""Id"" = entity.""{table}"".""EntityId""
                                         AND e.""TenantId"" = nullif(current_setting('app.tenant_id', true), '')::uuid));");
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in OwnTenantTables)
        {
            migrationBuilder.Sql($@"
                    DROP POLICY IF EXISTS ""TenantIsolation_{table}"" ON entity.""{table}"";
                    ALTER TABLE entity.""{table}"" NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE entity.""{table}"" DISABLE ROW LEVEL SECURITY;");
        }

        foreach (var table in EntityChildTables)
        {
            migrationBuilder.Sql($@"
                    DROP POLICY IF EXISTS ""TenantIsolation_{table}"" ON entity.""{table}"";
                    ALTER TABLE entity.""{table}"" NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE entity.""{table}"" DISABLE ROW LEVEL SECURITY;");
        }
    }
}
