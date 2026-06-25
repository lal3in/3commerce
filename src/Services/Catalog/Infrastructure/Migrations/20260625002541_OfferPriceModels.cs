using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class OfferPriceModels : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BillingPeriod",
            schema: "catalog",
            table: "Offers",
            type: "character varying(12)",
            maxLength: 12,
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateTable(
            name: "OfferPriceTier",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OfferId = table.Column<Guid>(type: "uuid", nullable: false),
                FromQuantity = table.Column<int>(type: "integer", nullable: false),
                UnitPriceMinor = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OfferPriceTier", x => x.Id);
                table.ForeignKey(
                    name: "FK_OfferPriceTier_Offers_OfferId",
                    column: x => x.OfferId,
                    principalSchema: "catalog",
                    principalTable: "Offers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OfferPriceTier_OfferId",
            schema: "catalog",
            table: "OfferPriceTier",
            column: "OfferId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OfferPriceTier",
            schema: "catalog");

        migrationBuilder.DropColumn(
            name: "BillingPeriod",
            schema: "catalog",
            table: "Offers");
    }
}
