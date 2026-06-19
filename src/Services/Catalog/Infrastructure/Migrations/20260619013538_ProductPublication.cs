using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class ProductPublication : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProductPublications",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                State = table.Column<int>(type: "integer", nullable: false),
                SlugOverride = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                TitleOverride = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                DescriptionOverride = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                SeoTitle = table.Column<string>(type: "character varying(70)", maxLength: 70, nullable: true),
                SeoDescription = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: true),
                FulfillmentSource = table.Column<int>(type: "integer", nullable: false),
                CountryOfOrigin = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                HarmonizedSystemCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_ProductPublications", x => x.Id));

        migrationBuilder.CreateTable(
            name: "ProductPublicationVariants",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PublicationId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: false),
                Visible = table.Column<bool>(type: "boolean", nullable: false),
                SkuOverride = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductPublicationVariants", x => x.Id);
                table.ForeignKey(
                    name: "FK_ProductPublicationVariants_ProductPublications_PublicationId",
                    column: x => x.PublicationId,
                    principalSchema: "catalog",
                    principalTable: "ProductPublications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_ProductPublications_TenantId_StorefrontId_ProductId", "ProductPublications", new[] { "TenantId", "StorefrontId", "ProductId" }, schema: "catalog", unique: true);
        migrationBuilder.CreateIndex("IX_ProductPublications_TenantId_StorefrontId_State", "ProductPublications", new[] { "TenantId", "StorefrontId", "State" }, schema: "catalog");
        migrationBuilder.CreateIndex("IX_ProductPublicationVariants_PublicationId_VariantId", "ProductPublicationVariants", new[] { "PublicationId", "VariantId" }, schema: "catalog", unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ProductPublicationVariants", schema: "catalog");
        migrationBuilder.DropTable(name: "ProductPublications", schema: "catalog");
    }
}
