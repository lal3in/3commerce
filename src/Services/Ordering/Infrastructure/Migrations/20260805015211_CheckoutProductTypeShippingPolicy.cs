using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class CheckoutProductTypeShippingPolicy : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ProductType",
            schema: "ordering",
            table: "OfferCopies",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "ProductTypeShippingPolicyCopies",
            schema: "ordering",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                RequiresShippingTypes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductTypeShippingPolicyCopies", x => x.TenantId);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ProductTypeShippingPolicyCopies",
            schema: "ordering");

        migrationBuilder.DropColumn(
            name: "ProductType",
            schema: "ordering",
            table: "OfferCopies");
    }
}
