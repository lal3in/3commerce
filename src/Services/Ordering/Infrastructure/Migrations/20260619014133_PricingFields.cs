using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PricingFields : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "SellingPriceMinor", schema: "ordering", table: "ProductCopies", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<Guid>(name: "StorefrontId", schema: "ordering", table: "ProductCopies", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<long>(name: "SupplierCostMinor", schema: "ordering", table: "ProductCopies", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<int>(name: "TaxMode", schema: "ordering", table: "ProductCopies", type: "integer", nullable: false, defaultValue: 1);
        migrationBuilder.Sql("""
            UPDATE ordering."ProductCopies"
            SET "SellingPriceMinor" = "MinPriceMinor",
                "TaxMode" = 1
            WHERE "SellingPriceMinor" = 0
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SellingPriceMinor", schema: "ordering", table: "ProductCopies");
        migrationBuilder.DropColumn(name: "StorefrontId", schema: "ordering", table: "ProductCopies");
        migrationBuilder.DropColumn(name: "SupplierCostMinor", schema: "ordering", table: "ProductCopies");
        migrationBuilder.DropColumn(name: "TaxMode", schema: "ordering", table: "ProductCopies");
    }
}
