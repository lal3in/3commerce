using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class CheckoutBillToAndBuyerIdentity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BillCity",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillCountry",
            schema: "ordering",
            table: "Orders",
            type: "character varying(2)",
            maxLength: 2,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillLine1",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillName",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillPostcode",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillRegion",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BuyerCompanyName",
            schema: "ordering",
            table: "Orders",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BuyerTaxId",
            schema: "ordering",
            table: "Orders",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BuyerTaxIdJurisdiction",
            schema: "ordering",
            table: "Orders",
            type: "character varying(10)",
            maxLength: 10,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "BuyerTaxIdScheme",
            schema: "ordering",
            table: "Orders",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillCity",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillCountry",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "character varying(2)",
            maxLength: 2,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillLine1",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillName",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillPostcode",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillRegion",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BuyerCompanyName",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BuyerTaxId",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BuyerTaxIdJurisdiction",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "character varying(10)",
            maxLength: 10,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "BuyerTaxIdScheme",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "integer",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "BillCity",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "BillCountry",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "BillLine1",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "BillName",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "BillPostcode",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "BillRegion",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "BuyerCompanyName",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "BuyerTaxId",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "BuyerTaxIdJurisdiction",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "BuyerTaxIdScheme",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "BillCity",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "BillCountry",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "BillLine1",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "BillName",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "BillPostcode",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "BillRegion",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "BuyerCompanyName",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "BuyerTaxId",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "BuyerTaxIdJurisdiction",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "BuyerTaxIdScheme",
            schema: "ordering",
            table: "CheckoutAttempts");
    }
}
