using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class OfferStorefrontPricing : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ActiveFrom",
            schema: "catalog",
            table: "Offers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ActiveUntil",
            schema: "catalog",
            table: "Offers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "StorefrontId",
            schema: "catalog",
            table: "Offers",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ActiveFrom",
            schema: "catalog",
            table: "Offers");

        migrationBuilder.DropColumn(
            name: "ActiveUntil",
            schema: "catalog",
            table: "Offers");

        migrationBuilder.DropColumn(
            name: "StorefrontId",
            schema: "catalog",
            table: "Offers");
    }
}
