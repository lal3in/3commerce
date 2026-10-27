using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PerLineTaxBreakdown : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ShippingExemptionReason",
            schema: "ordering",
            table: "Orders",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "ShippingTaxCategory",
            schema: "ordering",
            table: "Orders",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "ShippingTaxMinor",
            schema: "ordering",
            table: "Orders",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<int>(
            name: "ShippingTaxRateBps",
            schema: "ordering",
            table: "Orders",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "ShippingTaxableBaseMinor",
            schema: "ordering",
            table: "Orders",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<bool>(
            name: "TaxInclusive",
            schema: "ordering",
            table: "Orders",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "TaxScheme",
            schema: "ordering",
            table: "Orders",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "ExemptionReason",
            schema: "ordering",
            table: "OrderLines",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "TaxCategory",
            schema: "ordering",
            table: "OrderLines",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "TaxMinor",
            schema: "ordering",
            table: "OrderLines",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<int>(
            name: "TaxRateBps",
            schema: "ordering",
            table: "OrderLines",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "TaxableBaseMinor",
            schema: "ordering",
            table: "OrderLines",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<int>(
            name: "ShippingExemptionReason",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "ShippingTaxCategory",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "ShippingTaxMinor",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<int>(
            name: "ShippingTaxRateBps",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "ShippingTaxableBaseMinor",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<bool>(
            name: "TaxInclusive",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "TaxScheme",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "ExemptionReason",
            schema: "ordering",
            table: "CheckoutAttemptLines",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "TaxCategory",
            schema: "ordering",
            table: "CheckoutAttemptLines",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "TaxMinor",
            schema: "ordering",
            table: "CheckoutAttemptLines",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<int>(
            name: "TaxRateBps",
            schema: "ordering",
            table: "CheckoutAttemptLines",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "TaxableBaseMinor",
            schema: "ordering",
            table: "CheckoutAttemptLines",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ShippingExemptionReason",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "ShippingTaxCategory",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "ShippingTaxMinor",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "ShippingTaxRateBps",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "ShippingTaxableBaseMinor",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "TaxInclusive",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "TaxScheme",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "ExemptionReason",
            schema: "ordering",
            table: "OrderLines");

        migrationBuilder.DropColumn(
            name: "TaxCategory",
            schema: "ordering",
            table: "OrderLines");

        migrationBuilder.DropColumn(
            name: "TaxMinor",
            schema: "ordering",
            table: "OrderLines");

        migrationBuilder.DropColumn(
            name: "TaxRateBps",
            schema: "ordering",
            table: "OrderLines");

        migrationBuilder.DropColumn(
            name: "TaxableBaseMinor",
            schema: "ordering",
            table: "OrderLines");

        migrationBuilder.DropColumn(
            name: "ShippingExemptionReason",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "ShippingTaxCategory",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "ShippingTaxMinor",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "ShippingTaxRateBps",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "ShippingTaxableBaseMinor",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "TaxInclusive",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "TaxScheme",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "ExemptionReason",
            schema: "ordering",
            table: "CheckoutAttemptLines");

        migrationBuilder.DropColumn(
            name: "TaxCategory",
            schema: "ordering",
            table: "CheckoutAttemptLines");

        migrationBuilder.DropColumn(
            name: "TaxMinor",
            schema: "ordering",
            table: "CheckoutAttemptLines");

        migrationBuilder.DropColumn(
            name: "TaxRateBps",
            schema: "ordering",
            table: "CheckoutAttemptLines");

        migrationBuilder.DropColumn(
            name: "TaxableBaseMinor",
            schema: "ordering",
            table: "CheckoutAttemptLines");
    }
}
