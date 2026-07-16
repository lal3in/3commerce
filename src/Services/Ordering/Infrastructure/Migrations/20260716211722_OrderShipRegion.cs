using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class OrderShipRegion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ShipRegion",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ShipRegion",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ShipRegion",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "ShipRegion",
            schema: "ordering",
            table: "CheckoutAttempts");
    }
}
