using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class DiscountMinor : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "DiscountMinor", schema: "ordering", table: "Orders", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<long>(name: "DiscountMinor", schema: "ordering", table: "OrderLines", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<long>(name: "DiscountMinor", schema: "ordering", table: "CheckoutAttempts", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<long>(name: "DiscountMinor", schema: "ordering", table: "CheckoutAttemptLines", type: "bigint", nullable: false, defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "DiscountMinor", schema: "ordering", table: "Orders");
        migrationBuilder.DropColumn(name: "DiscountMinor", schema: "ordering", table: "OrderLines");
        migrationBuilder.DropColumn(name: "DiscountMinor", schema: "ordering", table: "CheckoutAttempts");
        migrationBuilder.DropColumn(name: "DiscountMinor", schema: "ordering", table: "CheckoutAttemptLines");
    }
}
