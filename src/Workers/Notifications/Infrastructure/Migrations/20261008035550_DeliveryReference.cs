using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Workers.Notifications.Infrastructure.Migrations;

/// <inheritdoc />
public partial class DeliveryReference : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Reference",
            schema: "notifications",
            table: "deliveries",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_deliveries_Reference",
            schema: "notifications",
            table: "deliveries",
            column: "Reference");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_deliveries_Reference",
            schema: "notifications",
            table: "deliveries");

        migrationBuilder.DropColumn(
            name: "Reference",
            schema: "notifications",
            table: "deliveries");
    }
}
