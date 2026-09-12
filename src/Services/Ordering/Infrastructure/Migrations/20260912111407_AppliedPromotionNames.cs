using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AppliedPromotionNames : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AppliedPromotionNames",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AppliedPromotionNames",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AppliedPromotionNames",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "AppliedPromotionNames",
            schema: "ordering",
            table: "CheckoutAttempts");
    }
}
