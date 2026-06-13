using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class OrderingCartCheckout : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Carts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CartKey = table.Column<string>(type: "text", nullable: true),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Carts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CheckoutStates",
            columns: table => new
            {
                CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                CurrentState = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PaymentIntentId = table.Column<string>(type: "text", nullable: true),
                AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                Email = table.Column<string>(type: "text", nullable: true),
                Currency = table.Column<string>(type: "text", nullable: true),
                TimeoutTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CheckoutStates", x => x.CorrelationId);
            });

        migrationBuilder.CreateTable(
            name: "Orders",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                Email = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                NetMinor = table.Column<long>(type: "bigint", nullable: false),
                TaxMinor = table.Column<long>(type: "bigint", nullable: false),
                ShippingMinor = table.Column<long>(type: "bigint", nullable: false),
                GrossMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                PaymentIntentId = table.Column<string>(type: "text", nullable: true),
                ShipName = table.Column<string>(type: "text", nullable: false),
                ShipLine1 = table.Column<string>(type: "text", nullable: false),
                ShipCity = table.Column<string>(type: "text", nullable: false),
                ShipPostcode = table.Column<string>(type: "text", nullable: false),
                ShipCountry = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Orders", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ProductCopies",
            columns: table => new
            {
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Slug = table.Column<string>(type: "text", nullable: false),
                Title = table.Column<string>(type: "text", nullable: false),
                MinPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                ImageUrl = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductCopies", x => x.ProductId);
            });

        migrationBuilder.CreateTable(
            name: "CartItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CartId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Slug = table.Column<string>(type: "text", nullable: false),
                Title = table.Column<string>(type: "text", nullable: false),
                ImageUrl = table.Column<string>(type: "text", nullable: true),
                UnitPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "text", nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CartItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_CartItems_Carts_CartId",
                    column: x => x.CartId,
                    principalTable: "Carts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "OrderLines",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "text", nullable: false),
                UnitPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false),
                FulfillmentSource = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderLines", x => x.Id);
                table.ForeignKey(
                    name: "FK_OrderLines_Orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CartItems_CartId",
            table: "CartItems",
            column: "CartId");

        migrationBuilder.CreateIndex(
            name: "IX_Carts_CartKey",
            table: "Carts",
            column: "CartKey");

        migrationBuilder.CreateIndex(
            name: "IX_Carts_UserId",
            table: "Carts",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_OrderLines_OrderId",
            table: "OrderLines",
            column: "OrderId");

        migrationBuilder.CreateIndex(
            name: "IX_ProductCopies_Slug",
            table: "ProductCopies",
            column: "Slug");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CartItems");

        migrationBuilder.DropTable(
            name: "CheckoutStates");

        migrationBuilder.DropTable(
            name: "OrderLines");

        migrationBuilder.DropTable(
            name: "ProductCopies");

        migrationBuilder.DropTable(
            name: "Carts");

        migrationBuilder.DropTable(
            name: "Orders");
    }
}
