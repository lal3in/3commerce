using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class CurrencyRegistry : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Currencies",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Symbol = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                DecimalPlaces = table.Column<int>(type: "integer", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Currencies", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Currencies_TenantId_Code",
            schema: "entity",
            table: "Currencies",
            columns: new[] { "TenantId", "Code" },
            unique: true);

        // Tenant-isolation RLS, matching every other tenant-scoped Entity table (EntityTenantTablesRls):
        // a session only sees/writes its own tenant's currencies unless it is the platform admin.
        migrationBuilder.Sql(@"
            ALTER TABLE entity.""Currencies"" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE entity.""Currencies"" FORCE ROW LEVEL SECURITY;
            CREATE POLICY ""TenantIsolation_Currencies"" ON entity.""Currencies""
                USING (current_setting('app.is_platform_admin', true) = 'true'
                    OR ""TenantId"" = nullif(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                    OR ""TenantId"" = nullif(current_setting('app.tenant_id', true), '')::uuid);");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"DROP POLICY IF EXISTS ""TenantIsolation_Currencies"" ON entity.""Currencies"";");
        migrationBuilder.DropTable(
            name: "Currencies",
            schema: "entity");
    }
}
