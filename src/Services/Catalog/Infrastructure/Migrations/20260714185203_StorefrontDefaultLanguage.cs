using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class StorefrontDefaultLanguage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DefaultLanguage",
            schema: "catalog",
            table: "Storefronts",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "en");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DefaultLanguage",
            schema: "catalog",
            table: "Storefronts");
    }
}
