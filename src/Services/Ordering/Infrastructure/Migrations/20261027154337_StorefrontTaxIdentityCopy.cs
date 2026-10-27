using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class StorefrontTaxIdentityCopy : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SellerEstablishmentKey",
            schema: "ordering",
            table: "StorefrontTaxCopies",
            type: "character varying(40)",
            maxLength: 40,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "TaxJurisdiction",
            schema: "ordering",
            table: "StorefrontTaxCopies",
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
            schema: "ordering",
            table: "StorefrontTaxCopies");

        migrationBuilder.DropColumn(
            name: "TaxJurisdiction",
            schema: "ordering",
            table: "StorefrontTaxCopies");
    }
}
