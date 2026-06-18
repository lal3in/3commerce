using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class CustomerEntityLinks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CustomerEntityLinks",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                CustomerPrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Role = table.Column<int>(type: "integer", nullable: false),
                EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                EffectiveTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LinkedByPrincipalId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CustomerEntityLinks", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CustomerEntityLinks_TenantId_CustomerPrincipalId",
            schema: "entity",
            table: "CustomerEntityLinks",
            columns: new[] { "TenantId", "CustomerPrincipalId" });

        migrationBuilder.CreateIndex(
            name: "IX_CustomerEntityLinks_TenantId_CustomerPrincipalId_EntityId_R~",
            schema: "entity",
            table: "CustomerEntityLinks",
            columns: new[] { "TenantId", "CustomerPrincipalId", "EntityId", "Role" },
            unique: true,
            filter: "\"EffectiveTo\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_CustomerEntityLinks_TenantId_EntityId",
            schema: "entity",
            table: "CustomerEntityLinks",
            columns: new[] { "TenantId", "EntityId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CustomerEntityLinks",
            schema: "entity");
    }
}
