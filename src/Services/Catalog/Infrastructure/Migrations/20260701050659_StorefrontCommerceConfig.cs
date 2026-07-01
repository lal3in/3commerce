using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;
/// <inheritdoc />
public partial class StorefrontCommerceConfig : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Currency",
            schema: "catalog",
            table: "Storefronts",
            type: "character varying(3)",
            maxLength: 3,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "PublicUrl",
            schema: "catalog",
            table: "Storefronts",
            type: "character varying(300)",
            maxLength: 300,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "TaxRateBasisPoints",
            schema: "catalog",
            table: "Storefronts",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "TaxRegime",
            schema: "catalog",
            table: "Storefronts",
            type: "character varying(24)",
            maxLength: 24,
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Currency",
            schema: "catalog",
            table: "Storefronts");

        migrationBuilder.DropColumn(
            name: "PublicUrl",
            schema: "catalog",
            table: "Storefronts");

        migrationBuilder.DropColumn(
            name: "TaxRateBasisPoints",
            schema: "catalog",
            table: "Storefronts");

        migrationBuilder.DropColumn(
            name: "TaxRegime",
            schema: "catalog",
            table: "Storefronts");
    }
}
