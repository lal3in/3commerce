using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Fulfillment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Dropship : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SupplierAvailabilities",
            schema: "fulfillment",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                SupplierSku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ExternalQuantity = table.Column<int>(type: "integer", nullable: true),
                LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SupplierAvailabilities", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SupplierOrders",
            schema: "fulfillment",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                ExternalReference = table.Column<string>(type: "text", nullable: true),
                TrackingNumber = table.Column<string>(type: "text", nullable: true),
                Carrier = table.Column<string>(type: "text", nullable: true),
                FailureReason = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SupplierOrders", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SupplierAvailabilities_TenantId_SupplierId_ProductId_Varian~",
            schema: "fulfillment",
            table: "SupplierAvailabilities",
            columns: new[] { "TenantId", "SupplierId", "ProductId", "VariantId" });

        migrationBuilder.CreateIndex(
            name: "IX_SupplierOrders_OrderId_SupplierId",
            schema: "fulfillment",
            table: "SupplierOrders",
            columns: new[] { "OrderId", "SupplierId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SupplierOrders_TenantId_OrderId",
            schema: "fulfillment",
            table: "SupplierOrders",
            columns: new[] { "TenantId", "OrderId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SupplierAvailabilities",
            schema: "fulfillment");

        migrationBuilder.DropTable(
            name: "SupplierOrders",
            schema: "fulfillment");
    }
}
