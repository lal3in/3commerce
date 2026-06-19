using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class TenantStorefrontProductModel : Migration
{
    private static readonly Guid DefaultTenantId = new("00000000-0000-0000-0000-000000000001");

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Products_CategoryId", schema: "catalog", table: "Products");
        migrationBuilder.DropIndex(name: "IX_Products_Slug", schema: "catalog", table: "Products");
        migrationBuilder.DropIndex(name: "IX_Categories_Slug", schema: "catalog", table: "Categories");

        migrationBuilder.AddColumn<string>(name: "Barcode", schema: "catalog", table: "Variants", type: "character varying(80)", maxLength: 80, nullable: true);
        migrationBuilder.AddColumn<int>(name: "HeightMm", schema: "catalog", table: "Variants", type: "integer", nullable: true);
        migrationBuilder.AddColumn<int>(name: "LengthMm", schema: "catalog", table: "Variants", type: "integer", nullable: true);
        migrationBuilder.AddColumn<int>(name: "WeightGrams", schema: "catalog", table: "Variants", type: "integer", nullable: true);
        migrationBuilder.AddColumn<int>(name: "WidthMm", schema: "catalog", table: "Variants", type: "integer", nullable: true);
        migrationBuilder.AddColumn<int>(name: "Kind", schema: "catalog", table: "Products", type: "integer", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<Guid>(name: "TenantId", schema: "catalog", table: "Products", type: "uuid", nullable: false, defaultValue: DefaultTenantId);
        migrationBuilder.AddColumn<int>(name: "SortOrder", schema: "catalog", table: "Categories", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<Guid>(name: "TenantId", schema: "catalog", table: "Categories", type: "uuid", nullable: false, defaultValue: DefaultTenantId);

        migrationBuilder.CreateTable(
            name: "ProductBundleComponents",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                BundleProductId = table.Column<Guid>(type: "uuid", nullable: false),
                ComponentProductId = table.Column<Guid>(type: "uuid", nullable: false),
                ComponentVariantId = table.Column<Guid>(type: "uuid", nullable: true),
                Quantity = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductBundleComponents", x => x.Id);
                table.ForeignKey(
                    name: "FK_ProductBundleComponents_Products_BundleProductId",
                    column: x => x.BundleProductId,
                    principalSchema: "catalog",
                    principalTable: "Products",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ProductIdentifiers",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Type = table.Column<int>(type: "integer", nullable: false),
                Value = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductIdentifiers", x => x.Id);
                table.ForeignKey(
                    name: "FK_ProductIdentifiers_Products_ProductId",
                    column: x => x.ProductId,
                    principalSchema: "catalog",
                    principalTable: "Products",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "StorefrontNavigationItems",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: false),
                CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_StorefrontNavigationItems", x => x.Id));

        migrationBuilder.CreateIndex("IX_Products_TenantId_CategoryId", "Products", new[] { "TenantId", "CategoryId" }, schema: "catalog");
        migrationBuilder.CreateIndex("IX_Products_TenantId_Slug", "Products", new[] { "TenantId", "Slug" }, schema: "catalog", unique: true);
        migrationBuilder.CreateIndex("IX_Categories_TenantId_Slug", "Categories", new[] { "TenantId", "Slug" }, schema: "catalog", unique: true);
        migrationBuilder.CreateIndex("IX_ProductBundleComponents_BundleProductId_ComponentProductId_ComponentVariantId", "ProductBundleComponents", new[] { "BundleProductId", "ComponentProductId", "ComponentVariantId" }, schema: "catalog", unique: true);
        migrationBuilder.CreateIndex("IX_ProductIdentifiers_ProductId", "ProductIdentifiers", "ProductId", schema: "catalog");
        migrationBuilder.CreateIndex("IX_ProductIdentifiers_Type_Value", "ProductIdentifiers", new[] { "Type", "Value" }, schema: "catalog");
        migrationBuilder.CreateIndex("IX_StorefrontNavigationItems_TenantId_StorefrontId_SortOrder", "StorefrontNavigationItems", new[] { "TenantId", "StorefrontId", "SortOrder" }, schema: "catalog");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ProductBundleComponents", schema: "catalog");
        migrationBuilder.DropTable(name: "ProductIdentifiers", schema: "catalog");
        migrationBuilder.DropTable(name: "StorefrontNavigationItems", schema: "catalog");
        migrationBuilder.DropIndex(name: "IX_Products_TenantId_CategoryId", schema: "catalog", table: "Products");
        migrationBuilder.DropIndex(name: "IX_Products_TenantId_Slug", schema: "catalog", table: "Products");
        migrationBuilder.DropIndex(name: "IX_Categories_TenantId_Slug", schema: "catalog", table: "Categories");
        migrationBuilder.DropColumn(name: "Barcode", schema: "catalog", table: "Variants");
        migrationBuilder.DropColumn(name: "HeightMm", schema: "catalog", table: "Variants");
        migrationBuilder.DropColumn(name: "LengthMm", schema: "catalog", table: "Variants");
        migrationBuilder.DropColumn(name: "WeightGrams", schema: "catalog", table: "Variants");
        migrationBuilder.DropColumn(name: "WidthMm", schema: "catalog", table: "Variants");
        migrationBuilder.DropColumn(name: "Kind", schema: "catalog", table: "Products");
        migrationBuilder.DropColumn(name: "TenantId", schema: "catalog", table: "Products");
        migrationBuilder.DropColumn(name: "SortOrder", schema: "catalog", table: "Categories");
        migrationBuilder.DropColumn(name: "TenantId", schema: "catalog", table: "Categories");
        migrationBuilder.CreateIndex("IX_Products_CategoryId", schema: "catalog", table: "Products", column: "CategoryId");
        migrationBuilder.CreateIndex("IX_Products_Slug", schema: "catalog", table: "Products", column: "Slug", unique: true);
        migrationBuilder.CreateIndex("IX_Categories_Slug", schema: "catalog", table: "Categories", column: "Slug", unique: true);
    }
}
