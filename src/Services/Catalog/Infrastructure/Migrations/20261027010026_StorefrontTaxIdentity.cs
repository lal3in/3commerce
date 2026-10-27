using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class StorefrontTaxIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SellerEstablishmentKey",
            schema: "catalog",
            table: "Storefronts",
            type: "character varying(40)",
            maxLength: 40,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "TaxJurisdiction",
            schema: "catalog",
            table: "Storefronts",
            type: "character varying(6)",
            maxLength: 6,
            nullable: false,
            defaultValue: "");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SellerEstablishmentKey",
            schema: "catalog",
            table: "Storefronts");

        migrationBuilder.DropColumn(
            name: "TaxJurisdiction",
            schema: "catalog",
            table: "Storefronts");
    }
}
