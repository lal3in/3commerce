using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class OfferCopySupplierCost : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Currency",
            schema: "ordering",
            table: "OfferCopies",
            type: "character varying(3)",
            maxLength: 3,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<long>(
            name: "SupplierCostMinor",
            schema: "ordering",
            table: "OfferCopies",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Currency",
            schema: "ordering",
            table: "OfferCopies");

        migrationBuilder.DropColumn(
            name: "SupplierCostMinor",
            schema: "ordering",
            table: "OfferCopies");
    }
}
