using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PromotionCopies : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PromotionCopies",
            schema: "ordering",
            columns: table => new
            {
                PromotionId = table.Column<Guid>(type: "uuid", nullable: false),
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
                Active = table.Column<bool>(type: "boolean", nullable: false),
                ActiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ActiveUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PromotionCopies", x => x.PromotionId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PromotionCopies_TenantId_StorefrontId_Active",
            schema: "ordering",
            table: "PromotionCopies",
            columns: new[] { "TenantId", "StorefrontId", "Active" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PromotionCopies",
            schema: "ordering");
    }
}
