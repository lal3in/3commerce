using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class VariantPerCurrencyPrices : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VariantPrices",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                PriceMinor = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VariantPrices", x => x.Id);
                table.ForeignKey(
                    name: "FK_VariantPrices_Variants_VariantId",
                    column: x => x.VariantId,
                    principalSchema: "catalog",
                    principalTable: "Variants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_VariantPrices_VariantId_Currency",
            schema: "catalog",
            table: "VariantPrices",
            columns: new[] { "VariantId", "Currency" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "VariantPrices",
            schema: "catalog");
    }
}
