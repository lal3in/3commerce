using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class OfferCopies : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OfferCopies",
            schema: "ordering",
            columns: table => new
            {
                OfferId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                FulfilmentType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false),
                Active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OfferCopies", x => x.OfferId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OfferCopies_TenantId_ProductId_VariantId",
            schema: "ordering",
            table: "OfferCopies",
            columns: new[] { "TenantId", "ProductId", "VariantId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OfferCopies",
            schema: "ordering");
    }
}
