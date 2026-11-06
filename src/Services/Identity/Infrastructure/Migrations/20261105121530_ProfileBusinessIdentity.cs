using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Identity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class ProfileBusinessIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BusinessCompanyName",
            schema: "identity",
            table: "Users",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BusinessTaxId",
            schema: "identity",
            table: "Users",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BusinessTaxIdJurisdiction",
            schema: "identity",
            table: "Users",
            type: "character varying(10)",
            maxLength: 10,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "BusinessTaxIdScheme",
            schema: "identity",
            table: "Users",
            type: "integer",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "BusinessCompanyName",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "BusinessTaxId",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "BusinessTaxIdJurisdiction",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "BusinessTaxIdScheme",
            schema: "identity",
            table: "Users");
    }
}
