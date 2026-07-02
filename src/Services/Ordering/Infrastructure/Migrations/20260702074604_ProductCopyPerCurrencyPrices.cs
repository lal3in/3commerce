using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProductCopyPerCurrencyPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductVariantCopyPrices",
                schema: "ordering",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PriceMinor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductVariantCopyPrices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductVariantCopyPrices_ProductVariantCopies_VariantId",
                        column: x => x.VariantId,
                        principalSchema: "ordering",
                        principalTable: "ProductVariantCopies",
                        principalColumn: "VariantId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductVariantCopyPrices_VariantId_Currency",
                schema: "ordering",
                table: "ProductVariantCopyPrices",
                columns: new[] { "VariantId", "Currency" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductVariantCopyPrices",
                schema: "ordering");
        }
    }
}
