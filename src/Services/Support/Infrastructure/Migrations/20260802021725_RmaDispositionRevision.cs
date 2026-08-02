using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Support.Infrastructure.Migrations;

/// <inheritdoc />
public partial class RmaDispositionRevision : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Revision",
            schema: "support",
            table: "RmaDispositions",
            type: "integer",
            nullable: false,
            defaultValue: 1);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Revision",
            schema: "support",
            table: "RmaDispositions");
    }
}
