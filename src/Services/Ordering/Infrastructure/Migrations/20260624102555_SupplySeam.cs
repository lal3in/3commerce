using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SupplySeam : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "FulfillmentSource",
            schema: "ordering",
            table: "OrderLines",
            newName: "FulfilmentType");

        migrationBuilder.RenameColumn(
            name: "FulfillmentSource",
            schema: "ordering",
            table: "CheckoutAttemptLines",
            newName: "FulfilmentType");

        migrationBuilder.AddColumn<int>(
            name: "BillingMode",
            schema: "ordering",
            table: "OrderLines",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "BillingMode",
            schema: "ordering",
            table: "CheckoutAttemptLines",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "BillingMode",
            schema: "ordering",
            table: "OrderLines");

        migrationBuilder.DropColumn(
            name: "BillingMode",
            schema: "ordering",
            table: "CheckoutAttemptLines");

        migrationBuilder.RenameColumn(
            name: "FulfilmentType",
            schema: "ordering",
            table: "OrderLines",
            newName: "FulfillmentSource");

        migrationBuilder.RenameColumn(
            name: "FulfilmentType",
            schema: "ordering",
            table: "CheckoutAttemptLines",
            newName: "FulfillmentSource");
    }
}
