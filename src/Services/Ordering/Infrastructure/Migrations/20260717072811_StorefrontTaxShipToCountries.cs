using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class StorefrontTaxShipToCountries : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ShipToCountries",
            schema: "ordering",
            table: "StorefrontTaxCopies",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ShipToCountries",
            schema: "ordering",
            table: "StorefrontTaxCopies");
    }
}
