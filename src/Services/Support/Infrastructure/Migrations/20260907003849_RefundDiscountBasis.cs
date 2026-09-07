using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Support.Infrastructure.Migrations;

/// <inheritdoc />
public partial class RefundDiscountBasis : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RefundFailureReason",
            schema: "support",
            table: "Rmas",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "DiscountMinor",
            schema: "support",
            table: "RmaRequestLines",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "DiscountMinor",
            schema: "support",
            table: "OrderSnapshotLines",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RefundFailureReason",
            schema: "support",
            table: "Rmas");

        migrationBuilder.DropColumn(
            name: "DiscountMinor",
            schema: "support",
            table: "RmaRequestLines");

        migrationBuilder.DropColumn(
            name: "DiscountMinor",
            schema: "support",
            table: "OrderSnapshotLines");
    }
}
