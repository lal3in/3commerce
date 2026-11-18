using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class EuDistanceSaleSource : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropPrimaryKey(
            name: "PK_EuDistanceSales",
            schema: "ordering",
            table: "EuDistanceSales");

        migrationBuilder.AddColumn<string>(
            name: "SourceReference",
            schema: "ordering",
            table: "EuDistanceSales",
            type: "character varying(200)",
            maxLength: 200,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddPrimaryKey(
            name: "PK_EuDistanceSales",
            schema: "ordering",
            table: "EuDistanceSales",
            columns: new[] { "OrderId", "DestinationCountry", "SourceReference" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropPrimaryKey(
            name: "PK_EuDistanceSales",
            schema: "ordering",
            table: "EuDistanceSales");

        migrationBuilder.DropColumn(
            name: "SourceReference",
            schema: "ordering",
            table: "EuDistanceSales");

        migrationBuilder.AddPrimaryKey(
            name: "PK_EuDistanceSales",
            schema: "ordering",
            table: "EuDistanceSales",
            columns: new[] { "OrderId", "DestinationCountry" });
    }
}
