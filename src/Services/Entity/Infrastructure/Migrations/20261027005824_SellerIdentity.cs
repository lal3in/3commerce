using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SellerIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Jurisdiction",
            schema: "entity",
            table: "EntityIdentifiers",
            type: "character varying(6)",
            maxLength: 6,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "SellerEstablishments",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Line2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                City = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Region = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                Postcode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                Jurisdiction = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SellerEstablishments", x => x.Id);
                table.ForeignKey(
                    name: "FK_SellerEstablishments_Entities_EntityId",
                    column: x => x.EntityId,
                    principalSchema: "entity",
                    principalTable: "Entities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SellerEstablishments_EntityId_Key",
            schema: "entity",
            table: "SellerEstablishments",
            columns: new[] { "EntityId", "Key" },
            unique: true);

        // Tenant isolation (ADR-0024), the same parent-join policy as the other Entity-aggregate children
        // keyed only by EntityId (EntityChildTablesRls): a row is visible/writable only when its parent
        // entity belongs to the scoped tenant, or under platform scope. FORCE so the owner role is bound too.
        migrationBuilder.Sql(@"
                ALTER TABLE entity.""SellerEstablishments"" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE entity.""SellerEstablishments"" FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS ""TenantIsolation_SellerEstablishments"" ON entity.""SellerEstablishments"";
                CREATE POLICY ""TenantIsolation_SellerEstablishments"" ON entity.""SellerEstablishments""
                    USING (current_setting('app.is_platform_admin', true) = 'true'
                        OR EXISTS (SELECT 1 FROM entity.""Entities"" e
                                   WHERE e.""Id"" = entity.""SellerEstablishments"".""EntityId""
                                     AND e.""TenantId"" = nullif(current_setting('app.tenant_id', true), '')::uuid))
                    WITH CHECK (current_setting('app.is_platform_admin', true) = 'true'
                        OR EXISTS (SELECT 1 FROM entity.""Entities"" e
                                   WHERE e.""Id"" = entity.""SellerEstablishments"".""EntityId""
                                     AND e.""TenantId"" = nullif(current_setting('app.tenant_id', true), '')::uuid));");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"DROP POLICY IF EXISTS ""TenantIsolation_SellerEstablishments"" ON entity.""SellerEstablishments"";");
        migrationBuilder.DropTable(
            name: "SellerEstablishments",
            schema: "entity");

        migrationBuilder.DropColumn(
            name: "Jurisdiction",
            schema: "entity",
            table: "EntityIdentifiers");
    }
}
