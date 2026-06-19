using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Identity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AddressTenantScope : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "TenantId",
            schema: "identity",
            table: "Addresses",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.Sql("""
            UPDATE identity."Addresses" a
            SET "TenantId" = u."TenantId"
            FROM identity."Users" u
            WHERE a."UserId" = u."Id"
              AND u."TenantId" IS NOT NULL
            """);

        migrationBuilder.CreateIndex(
            name: "IX_Addresses_TenantId_UserId",
            schema: "identity",
            table: "Addresses",
            columns: new[] { "TenantId", "UserId" });

        // Tenant-isolate Addresses under FORCE RLS, mirroring Users (ADR-0024). The profile
        // endpoints already run address reads/writes inside a tenant scope (ProfileEndpoints.Scope),
        // so the policy is the DB-level backstop (fail closed with no context).
        migrationBuilder.Sql("""
            ALTER TABLE identity."Addresses" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE identity."Addresses" FORCE ROW LEVEL SECURITY;

            CREATE POLICY addresses_tenant_isolation ON identity."Addresses"
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
            DROP POLICY IF EXISTS addresses_tenant_isolation ON identity."Addresses";
            ALTER TABLE identity."Addresses" NO FORCE ROW LEVEL SECURITY;
            ALTER TABLE identity."Addresses" DISABLE ROW LEVEL SECURITY;
            """);

        migrationBuilder.DropIndex(
            name: "IX_Addresses_TenantId_UserId",
            schema: "identity",
            table: "Addresses");

        migrationBuilder.DropColumn(
            name: "TenantId",
            schema: "identity",
            table: "Addresses");
    }
}
