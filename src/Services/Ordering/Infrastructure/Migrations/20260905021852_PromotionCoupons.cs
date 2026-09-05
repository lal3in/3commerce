using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PromotionCoupons : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Code",
            schema: "ordering",
            table: "PromotionCopies",
            type: "character varying(40)",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MaxRedemptions",
            schema: "ordering",
            table: "PromotionCopies",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MaxRedemptionsPerCustomer",
            schema: "ordering",
            table: "PromotionCopies",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RedeemedCount",
            schema: "ordering",
            table: "PromotionCopies",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "CouponCode",
            schema: "ordering",
            table: "Orders",
            type: "character varying(40)",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CouponCode",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "character varying(40)",
            maxLength: 40,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "PromotionRedemptions",
            schema: "ordering",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PromotionId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                CustomerKey = table.Column<string>(type: "character varying(340)", maxLength: 340, nullable: false),
                Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PromotionRedemptions", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PromotionCopies_TenantId_Code",
            schema: "ordering",
            table: "PromotionCopies",
            columns: new[] { "TenantId", "Code" });

        migrationBuilder.CreateIndex(
            name: "IX_PromotionRedemptions_OrderId",
            schema: "ordering",
            table: "PromotionRedemptions",
            column: "OrderId");

        migrationBuilder.CreateIndex(
            name: "IX_PromotionRedemptions_PromotionId_CustomerKey",
            schema: "ordering",
            table: "PromotionRedemptions",
            columns: new[] { "PromotionId", "CustomerKey" });

        migrationBuilder.CreateIndex(
            name: "IX_PromotionRedemptions_PromotionId_OrderId",
            schema: "ordering",
            table: "PromotionRedemptions",
            columns: new[] { "PromotionId", "OrderId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PromotionRedemptions",
            schema: "ordering");

        migrationBuilder.DropIndex(
            name: "IX_PromotionCopies_TenantId_Code",
            schema: "ordering",
            table: "PromotionCopies");

        migrationBuilder.DropColumn(
            name: "Code",
            schema: "ordering",
            table: "PromotionCopies");

        migrationBuilder.DropColumn(
            name: "MaxRedemptions",
            schema: "ordering",
            table: "PromotionCopies");

        migrationBuilder.DropColumn(
            name: "MaxRedemptionsPerCustomer",
            schema: "ordering",
            table: "PromotionCopies");

        migrationBuilder.DropColumn(
            name: "RedeemedCount",
            schema: "ordering",
            table: "PromotionCopies");

        migrationBuilder.DropColumn(
            name: "CouponCode",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "CouponCode",
            schema: "ordering",
            table: "CheckoutAttempts");
    }
}
