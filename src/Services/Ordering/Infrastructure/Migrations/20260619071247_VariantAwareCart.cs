using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class VariantAwareCart : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "VariantId", schema: "ordering", table: "OrderLines", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>(name: "VariantSku", schema: "ordering", table: "OrderLines", type: "text", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "VariantId", schema: "ordering", table: "CheckoutAttemptLines", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>(name: "VariantSku", schema: "ordering", table: "CheckoutAttemptLines", type: "text", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "VariantId", schema: "ordering", table: "CartItems", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>(name: "VariantSku", schema: "ordering", table: "CartItems", type: "text", nullable: true);

        migrationBuilder.CreateTable(
            name: "ProductVariantCopies",
            schema: "ordering",
            columns: table => new
            {
                VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Sku = table.Column<string>(type: "text", nullable: false),
                PriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                StockQuantity = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductVariantCopies", x => x.VariantId);
                table.ForeignKey(
                    name: "FK_ProductVariantCopies_ProductCopies_ProductId",
                    column: x => x.ProductId,
                    principalSchema: "ordering",
                    principalTable: "ProductCopies",
                    principalColumn: "ProductId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_ProductVariantCopies_ProductId_Sku", "ProductVariantCopies", new[] { "ProductId", "Sku" }, schema: "ordering");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ProductVariantCopies", schema: "ordering");
        migrationBuilder.DropColumn(name: "VariantId", schema: "ordering", table: "OrderLines");
        migrationBuilder.DropColumn(name: "VariantSku", schema: "ordering", table: "OrderLines");
        migrationBuilder.DropColumn(name: "VariantId", schema: "ordering", table: "CheckoutAttemptLines");
        migrationBuilder.DropColumn(name: "VariantSku", schema: "ordering", table: "CheckoutAttemptLines");
        migrationBuilder.DropColumn(name: "VariantId", schema: "ordering", table: "CartItems");
        migrationBuilder.DropColumn(name: "VariantSku", schema: "ordering", table: "CartItems");
    }
}
