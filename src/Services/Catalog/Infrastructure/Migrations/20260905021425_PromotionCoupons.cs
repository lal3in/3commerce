using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PromotionCoupons : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Code",
            schema: "catalog",
            table: "Promotions",
            type: "character varying(40)",
            maxLength: 40,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MaxRedemptions",
            schema: "catalog",
            table: "Promotions",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MaxRedemptionsPerCustomer",
            schema: "catalog",
            table: "Promotions",
            type: "integer",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Promotions_TenantId_Code",
            schema: "catalog",
            table: "Promotions",
            columns: new[] { "TenantId", "Code" },
            unique: true,
            filter: "\"Code\" IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Promotions_TenantId_Code",
            schema: "catalog",
            table: "Promotions");

        migrationBuilder.DropColumn(
            name: "Code",
            schema: "catalog",
            table: "Promotions");

        migrationBuilder.DropColumn(
            name: "MaxRedemptions",
            schema: "catalog",
            table: "Promotions");

        migrationBuilder.DropColumn(
            name: "MaxRedemptionsPerCustomer",
            schema: "catalog",
            table: "Promotions");
    }
}
