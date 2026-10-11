using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SupplyOfferCostSnapshot : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "BookedSupplierCostMinor",
            schema: "ordering",
            table: "OrderLines",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "SupplierUnitCostMinor",
            schema: "ordering",
            table: "OrderLines",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "SupplierUnitCostMinor",
            schema: "ordering",
            table: "CheckoutAttemptLines",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "BookedSupplierCostMinor",
            schema: "ordering",
            table: "OrderLines");

        migrationBuilder.DropColumn(
            name: "SupplierUnitCostMinor",
            schema: "ordering",
            table: "OrderLines");

        migrationBuilder.DropColumn(
            name: "SupplierUnitCostMinor",
            schema: "ordering",
            table: "CheckoutAttemptLines");
    }
}
