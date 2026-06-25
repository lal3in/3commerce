using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SubscriptionBillingFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "BillingPeriod",
            schema: "ordering",
            table: "OrderLines",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "BillingPeriod",
            schema: "ordering",
            table: "OfferCopies",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "PricingModel",
            schema: "ordering",
            table: "OfferCopies",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "BillingPeriod",
            schema: "ordering",
            table: "CheckoutAttemptLines",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "BillingPeriod",
            schema: "ordering",
            table: "OrderLines");

        migrationBuilder.DropColumn(
            name: "BillingPeriod",
            schema: "ordering",
            table: "OfferCopies");

        migrationBuilder.DropColumn(
            name: "PricingModel",
            schema: "ordering",
            table: "OfferCopies");

        migrationBuilder.DropColumn(
            name: "BillingPeriod",
            schema: "ordering",
            table: "CheckoutAttemptLines");
    }
}
