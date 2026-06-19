using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class CheckoutAttempts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "PublicOrderNumber", schema: "ordering", table: "Orders", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<Guid>(name: "StorefrontId", schema: "ordering", table: "Orders", type: "uuid", nullable: true);

        migrationBuilder.CreateTable(
            name: "CheckoutAttempts",
            schema: "ordering",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: false),
                Email = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                NetMinor = table.Column<long>(type: "bigint", nullable: false),
                ShippingMinor = table.Column<long>(type: "bigint", nullable: false),
                TaxMinor = table.Column<long>(type: "bigint", nullable: false),
                GrossMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                PaymentIntentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                CampaignRef = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                ShipName = table.Column<string>(type: "text", nullable: false),
                ShipLine1 = table.Column<string>(type: "text", nullable: false),
                ShipCity = table.Column<string>(type: "text", nullable: false),
                ShipPostcode = table.Column<string>(type: "text", nullable: false),
                ShipCountry = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_CheckoutAttempts", x => x.Id));

        migrationBuilder.CreateTable(
            name: "OrderNumberSequences",
            schema: "ordering",
            columns: table => new
            {
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: false),
                NextNumber = table.Column<long>(type: "bigint", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_OrderNumberSequences", x => x.StorefrontId));

        migrationBuilder.CreateTable(
            name: "CheckoutAttemptLines",
            schema: "ordering",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CheckoutAttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "text", nullable: false),
                UnitPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false),
                FulfillmentSource = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CheckoutAttemptLines", x => x.Id);
                table.ForeignKey(
                    name: "FK_CheckoutAttemptLines_CheckoutAttempts_CheckoutAttemptId",
                    column: x => x.CheckoutAttemptId,
                    principalSchema: "ordering",
                    principalTable: "CheckoutAttempts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_Orders_StorefrontId_PublicOrderNumber", "Orders", new[] { "StorefrontId", "PublicOrderNumber" }, schema: "ordering", unique: true);
        migrationBuilder.CreateIndex("IX_CheckoutAttemptLines_CheckoutAttemptId", "CheckoutAttemptLines", "CheckoutAttemptId", schema: "ordering");
        migrationBuilder.CreateIndex("IX_CheckoutAttempts_StorefrontId_Status", "CheckoutAttempts", new[] { "StorefrontId", "Status" }, schema: "ordering");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CheckoutAttemptLines", schema: "ordering");
        migrationBuilder.DropTable(name: "OrderNumberSequences", schema: "ordering");
        migrationBuilder.DropTable(name: "CheckoutAttempts", schema: "ordering");
        migrationBuilder.DropIndex(name: "IX_Orders_StorefrontId_PublicOrderNumber", schema: "ordering", table: "Orders");
        migrationBuilder.DropColumn(name: "PublicOrderNumber", schema: "ordering", table: "Orders");
        migrationBuilder.DropColumn(name: "StorefrontId", schema: "ordering", table: "Orders");
    }
}
