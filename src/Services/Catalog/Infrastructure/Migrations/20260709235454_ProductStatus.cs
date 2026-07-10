using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class ProductStatus : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Default existing rows to Active (1) — Inactive is 2; there is no 0 value.
        // The public search/detail gate filters on Status = 1, so existing products must land Active.
        migrationBuilder.AddColumn<int>(
            name: "Status",
            schema: "catalog",
            table: "Products",
            type: "integer",
            nullable: false,
            defaultValue: 1);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Status",
            schema: "catalog",
            table: "Products");
    }
}
