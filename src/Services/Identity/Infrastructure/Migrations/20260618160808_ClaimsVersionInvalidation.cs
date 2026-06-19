using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Identity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class ClaimsVersionInvalidation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ClaimsVersion",
            schema: "identity",
            table: "Sessions",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<int>(
            name: "ClaimsVersion",
            schema: "identity",
            table: "Principals",
            type: "integer",
            nullable: false,
            defaultValue: 1);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ClaimsVersion",
            schema: "identity",
            table: "Sessions");

        migrationBuilder.DropColumn(
            name: "ClaimsVersion",
            schema: "identity",
            table: "Principals");
    }
}
