using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class ProductShipRulesAndCatalogSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ShipRules",
            schema: "catalog",
            table: "Products",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");

        migrationBuilder.CreateTable(
            name: "TenantCatalogSettings",
            schema: "catalog",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                RequireProductShipRules = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TenantCatalogSettings", x => x.TenantId);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TenantCatalogSettings",
            schema: "catalog");

        migrationBuilder.DropColumn(
            name: "ShipRules",
            schema: "catalog",
            table: "Products");
    }
}
