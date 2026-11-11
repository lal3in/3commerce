using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SubscriptionOssTaxTerms : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "TaxDestinationCountry",
            schema: "payments",
            table: "Subscriptions",
            type: "character varying(2)",
            maxLength: 2,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TaxScheme",
            schema: "payments",
            table: "Subscriptions",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TaxDestinationCountry",
            schema: "payments",
            table: "Subscriptions");

        migrationBuilder.DropColumn(
            name: "TaxScheme",
            schema: "payments",
            table: "Subscriptions");
    }
}
