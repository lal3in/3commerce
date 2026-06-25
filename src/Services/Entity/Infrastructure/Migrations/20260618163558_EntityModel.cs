using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class EntityModel : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "LegalName",
            schema: "entity",
            table: "Entities",
            type: "character varying(200)",
            maxLength: 200,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "TradingName",
            schema: "entity",
            table: "Entities",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Type",
            schema: "entity",
            table: "Entities",
            type: "integer",
            nullable: false,
            defaultValue: 99);

        migrationBuilder.Sql("""
            UPDATE entity."Entities"
            SET "LegalName" = "DisplayName"
            WHERE "LegalName" = '';
            """);

        migrationBuilder.CreateTable(
            name: "EntityProfiles",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Role = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EntityProfiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_EntityProfiles_Entities_EntityId",
                    column: x => x.EntityId,
                    principalSchema: "entity",
                    principalTable: "Entities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Entities_TenantId_Type",
            schema: "entity",
            table: "Entities",
            columns: new[] { "TenantId", "Type" });

        migrationBuilder.CreateIndex(
            name: "IX_EntityProfiles_EntityId_Role",
            schema: "entity",
            table: "EntityProfiles",
            columns: new[] { "EntityId", "Role" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_EntityProfiles_Role_Status",
            schema: "entity",
            table: "EntityProfiles",
            columns: new[] { "Role", "Status" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "EntityProfiles", schema: "entity");

        migrationBuilder.DropIndex(
            name: "IX_Entities_TenantId_Type",
            schema: "entity",
            table: "Entities");

        migrationBuilder.DropColumn(name: "LegalName", schema: "entity", table: "Entities");
        migrationBuilder.DropColumn(name: "TradingName", schema: "entity", table: "Entities");
        migrationBuilder.DropColumn(name: "Type", schema: "entity", table: "Entities");
    }
}
