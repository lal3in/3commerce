using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;
/// <inheritdoc />
public partial class PaymentOptionAudit : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PaymentInstrumentSummary",
            schema: "ordering",
            table: "Orders",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PaymentOption",
            schema: "ordering",
            table: "Orders",
            type: "character varying(40)",
            maxLength: 40,
            nullable: false,
            defaultValue: "CreditCard");

        migrationBuilder.AddColumn<string>(
            name: "PaymentProvider",
            schema: "ordering",
            table: "Orders",
            type: "character varying(40)",
            maxLength: 40,
            nullable: false,
            defaultValue: "Stripe");

        migrationBuilder.AddColumn<string>(
            name: "PaymentInstrumentSummary",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PaymentOption",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "character varying(40)",
            maxLength: 40,
            nullable: false,
            defaultValue: "CreditCard");

        migrationBuilder.AddColumn<string>(
            name: "PaymentProvider",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "character varying(40)",
            maxLength: 40,
            nullable: false,
            defaultValue: "Stripe");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PaymentInstrumentSummary",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "PaymentOption",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "PaymentProvider",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "PaymentInstrumentSummary",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "PaymentOption",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "PaymentProvider",
            schema: "ordering",
            table: "CheckoutAttempts");
    }
}
