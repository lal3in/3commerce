using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Fulfillment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class OrderHolds : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "HeldOrders",
            schema: "fulfillment",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                Fulfilled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HeldOrders", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "OrderHolds",
            schema: "fulfillment",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                Reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Note = table.Column<string>(type: "text", nullable: true),
                PlacedBy = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderHolds", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_HeldOrders_OrderId",
            schema: "fulfillment",
            table: "HeldOrders",
            column: "OrderId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OrderHolds_TenantId_OrderId_Status",
            schema: "fulfillment",
            table: "OrderHolds",
            columns: new[] { "TenantId", "OrderId", "Status" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "HeldOrders",
            schema: "fulfillment");

        migrationBuilder.DropTable(
            name: "OrderHolds",
            schema: "fulfillment");
    }
}
