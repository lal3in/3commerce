using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class ReadinessAsOf : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "AsOf",
            schema: "catalog",
            table: "StorefrontPaymentReadiness",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "AsOf",
            schema: "catalog",
            table: "StorefrontCarrierReadiness",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AsOf",
            schema: "catalog",
            table: "StorefrontPaymentReadiness");

        migrationBuilder.DropColumn(
            name: "AsOf",
            schema: "catalog",
            table: "StorefrontCarrierReadiness");
    }
}
