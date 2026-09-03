using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Promotions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Promotions",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: true),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                MinimumAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                MinimumQuantity = table.Column<int>(type: "integer", nullable: false),
                GrantsFreeShipping = table.Column<bool>(type: "boolean", nullable: false),
                PercentOff = table.Column<int>(type: "integer", nullable: false),
                DiscountAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                Combinable = table.Column<bool>(type: "boolean", nullable: false),
                ActiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ActiveUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Promotions", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Promotions_TenantId_ProductId",
            schema: "catalog",
            table: "Promotions",
            columns: new[] { "TenantId", "ProductId" });

        migrationBuilder.CreateIndex(
            name: "IX_Promotions_TenantId_StorefrontId",
            schema: "catalog",
            table: "Promotions",
            columns: new[] { "TenantId", "StorefrontId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Promotions",
            schema: "catalog");
    }
}
