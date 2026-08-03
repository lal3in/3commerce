using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class VariantPackageDimensions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PackageHeightMm",
            schema: "catalog",
            table: "Variants",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PackageLengthMm",
            schema: "catalog",
            table: "Variants",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PackageWeightGrams",
            schema: "catalog",
            table: "Variants",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PackageWidthMm",
            schema: "catalog",
            table: "Variants",
            type: "integer",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PackageHeightMm",
            schema: "catalog",
            table: "Variants");

        migrationBuilder.DropColumn(
            name: "PackageLengthMm",
            schema: "catalog",
            table: "Variants");

        migrationBuilder.DropColumn(
            name: "PackageWeightGrams",
            schema: "catalog",
            table: "Variants");

        migrationBuilder.DropColumn(
            name: "PackageWidthMm",
            schema: "catalog",
            table: "Variants");
    }
}
