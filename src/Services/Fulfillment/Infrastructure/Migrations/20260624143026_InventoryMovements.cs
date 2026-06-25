using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Fulfillment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InventoryMovements : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InventoryMovements",
            schema: "fulfillment",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false),
                ReferenceType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                ReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InventoryMovements", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_InventoryMovements_InventoryItemId",
            schema: "fulfillment",
            table: "InventoryMovements",
            column: "InventoryItemId");

        migrationBuilder.CreateIndex(
            name: "IX_InventoryMovements_ReferenceId_Type",
            schema: "fulfillment",
            table: "InventoryMovements",
            columns: new[] { "ReferenceId", "Type" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "InventoryMovements",
            schema: "fulfillment");
    }
}
