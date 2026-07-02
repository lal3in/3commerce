using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class StorefrontTaxCopy : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "StorefrontTaxCopies",
            schema: "ordering",
            columns: table => new
            {
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                TaxRateBasisPoints = table.Column<int>(type: "integer", nullable: false),
                IsLive = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorefrontTaxCopies", x => x.StorefrontId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_StorefrontTaxCopies_Currency_IsLive",
            schema: "ordering",
            table: "StorefrontTaxCopies",
            columns: new[] { "Currency", "IsLive" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "StorefrontTaxCopies",
            schema: "ordering");
    }
}
