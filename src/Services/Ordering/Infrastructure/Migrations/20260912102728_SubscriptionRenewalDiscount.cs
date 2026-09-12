using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SubscriptionRenewalDiscount : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "AppliesToRenewals",
            schema: "ordering",
            table: "PromotionCopies",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<long>(
            name: "RenewalDiscountMinor",
            schema: "ordering",
            table: "OrderLines",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "RenewalDiscountMinor",
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
            name: "AppliesToRenewals",
            schema: "ordering",
            table: "PromotionCopies");

        migrationBuilder.DropColumn(
            name: "RenewalDiscountMinor",
            schema: "ordering",
            table: "OrderLines");

        migrationBuilder.DropColumn(
            name: "RenewalDiscountMinor",
            schema: "ordering",
            table: "CheckoutAttemptLines");
    }
}
