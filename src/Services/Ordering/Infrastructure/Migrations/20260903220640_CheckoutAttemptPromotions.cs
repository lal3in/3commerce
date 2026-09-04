using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class CheckoutAttemptPromotions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AppliedPromotionIds",
            schema: "ordering",
            table: "Orders",
            type: "character varying(400)",
            maxLength: 400,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "FreeShippingApplied",
            schema: "ordering",
            table: "Orders",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<long>(
            name: "PromotionDiscountMinor",
            schema: "ordering",
            table: "Orders",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<string>(
            name: "AppliedPromotionIds",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "character varying(400)",
            maxLength: 400,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "FreeShippingApplied",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<long>(
            name: "PromotionDiscountMinor",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AppliedPromotionIds",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "FreeShippingApplied",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "PromotionDiscountMinor",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "AppliedPromotionIds",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "FreeShippingApplied",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "PromotionDiscountMinor",
            schema: "ordering",
            table: "CheckoutAttempts");
    }
}
