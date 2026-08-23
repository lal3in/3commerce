using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class WarehouseCollectAndDelivered : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "CollectAtWarehouse",
            schema: "ordering",
            table: "Orders",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "WarehouseCity",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WarehouseCountry",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WarehouseLine1",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WarehouseName",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WarehousePostcode",
            schema: "ordering",
            table: "Orders",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "CollectAtWarehouse",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "WarehouseCity",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WarehouseCountry",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WarehouseLine1",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WarehouseName",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WarehousePostcode",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "text",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "SupplierWarehouseCopies",
            schema: "ordering",
            columns: table => new
            {
                SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Line2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                City = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Region = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Postcode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SupplierWarehouseCopies", x => x.SupplierId);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SupplierWarehouseCopies",
            schema: "ordering");

        migrationBuilder.DropColumn(
            name: "CollectAtWarehouse",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "WarehouseCity",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "WarehouseCountry",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "WarehouseLine1",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "WarehouseName",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "WarehousePostcode",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "CollectAtWarehouse",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "WarehouseCity",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "WarehouseCountry",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "WarehouseLine1",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "WarehouseName",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "WarehousePostcode",
            schema: "ordering",
            table: "CheckoutAttempts");
    }
}
