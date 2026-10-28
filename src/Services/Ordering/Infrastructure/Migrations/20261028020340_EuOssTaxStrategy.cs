using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class EuOssTaxStrategy : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "TaxDestinationCountry",
            schema: "ordering",
            table: "Orders",
            type: "character varying(2)",
            maxLength: 2,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "TaxJurisdiction",
            schema: "ordering",
            table: "Orders",
            type: "character varying(6)",
            maxLength: 6,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "TaxDestinationCountry",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "character varying(2)",
            maxLength: 2,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "TaxJurisdiction",
            schema: "ordering",
            table: "CheckoutAttempts",
            type: "character varying(6)",
            maxLength: 6,
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateTable(
            name: "EuDistanceSales",
            schema: "ordering",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Year = table.Column<int>(type: "integer", nullable: false),
                HomeCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                DestinationCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                NetMinor = table.Column<long>(type: "bigint", nullable: false),
                TaxScheme = table.Column<int>(type: "integer", nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EuDistanceSales", x => x.OrderId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_EuDistanceSales_TenantId_Year_Currency",
            schema: "ordering",
            table: "EuDistanceSales",
            columns: new[] { "TenantId", "Year", "Currency" })
            .Annotation("Npgsql:IndexInclude", new[] { "NetMinor" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "EuDistanceSales",
            schema: "ordering");

        migrationBuilder.DropColumn(
            name: "TaxDestinationCountry",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "TaxJurisdiction",
            schema: "ordering",
            table: "Orders");

        migrationBuilder.DropColumn(
            name: "TaxDestinationCountry",
            schema: "ordering",
            table: "CheckoutAttempts");

        migrationBuilder.DropColumn(
            name: "TaxJurisdiction",
            schema: "ordering",
            table: "CheckoutAttempts");
    }
}
