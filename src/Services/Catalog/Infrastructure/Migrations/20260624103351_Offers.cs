using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Offers : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Offers",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                SupplyCategory = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                FulfilmentType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                PricingModel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                PriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Offers", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Offers_TenantId_ProductId_VariantId",
            schema: "catalog",
            table: "Offers",
            columns: new[] { "TenantId", "ProductId", "VariantId" });

        migrationBuilder.CreateIndex(
            name: "IX_Offers_TenantId_SupplierId",
            schema: "catalog",
            table: "Offers",
            columns: new[] { "TenantId", "SupplierId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Offers",
            schema: "catalog");
    }
}
