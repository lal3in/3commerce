using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SavedCardBillingAddress : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BillingCity",
            schema: "payments",
            table: "SavedPaymentMethods",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillingCountry",
            schema: "payments",
            table: "SavedPaymentMethods",
            type: "character varying(2)",
            maxLength: 2,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillingLine1",
            schema: "payments",
            table: "SavedPaymentMethods",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillingName",
            schema: "payments",
            table: "SavedPaymentMethods",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillingPostcode",
            schema: "payments",
            table: "SavedPaymentMethods",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BillingRegion",
            schema: "payments",
            table: "SavedPaymentMethods",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "BillingCity",
            schema: "payments",
            table: "SavedPaymentMethods");

        migrationBuilder.DropColumn(
            name: "BillingCountry",
            schema: "payments",
            table: "SavedPaymentMethods");

        migrationBuilder.DropColumn(
            name: "BillingLine1",
            schema: "payments",
            table: "SavedPaymentMethods");

        migrationBuilder.DropColumn(
            name: "BillingName",
            schema: "payments",
            table: "SavedPaymentMethods");

        migrationBuilder.DropColumn(
            name: "BillingPostcode",
            schema: "payments",
            table: "SavedPaymentMethods");

        migrationBuilder.DropColumn(
            name: "BillingRegion",
            schema: "payments",
            table: "SavedPaymentMethods");
    }
}
