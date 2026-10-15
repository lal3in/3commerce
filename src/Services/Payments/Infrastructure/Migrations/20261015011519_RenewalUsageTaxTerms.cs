using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class RenewalUsageTaxTerms : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "TaxExempt",
            schema: "payments",
            table: "Subscriptions",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "TaxInclusive",
            schema: "payments",
            table: "Subscriptions",
            type: "boolean",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "TaxInclusive",
            schema: "payments",
            table: "StorefrontLedgerAccounts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "TaxRateBasisPoints",
            schema: "payments",
            table: "StorefrontLedgerAccounts",
            type: "integer",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TaxExempt",
            schema: "payments",
            table: "Subscriptions");

        migrationBuilder.DropColumn(
            name: "TaxInclusive",
            schema: "payments",
            table: "Subscriptions");

        migrationBuilder.DropColumn(
            name: "TaxInclusive",
            schema: "payments",
            table: "StorefrontLedgerAccounts");

        migrationBuilder.DropColumn(
            name: "TaxRateBasisPoints",
            schema: "payments",
            table: "StorefrontLedgerAccounts");
    }
}
