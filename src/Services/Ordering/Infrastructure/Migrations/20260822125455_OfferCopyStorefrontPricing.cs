using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class OfferCopyStorefrontPricing : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ActiveFrom",
            schema: "ordering",
            table: "OfferCopies",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ActiveUntil",
            schema: "ordering",
            table: "OfferCopies",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "PriceMinor",
            schema: "ordering",
            table: "OfferCopies",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<Guid>(
            name: "StorefrontId",
            schema: "ordering",
            table: "OfferCopies",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ActiveFrom",
            schema: "ordering",
            table: "OfferCopies");

        migrationBuilder.DropColumn(
            name: "ActiveUntil",
            schema: "ordering",
            table: "OfferCopies");

        migrationBuilder.DropColumn(
            name: "PriceMinor",
            schema: "ordering",
            table: "OfferCopies");

        migrationBuilder.DropColumn(
            name: "StorefrontId",
            schema: "ordering",
            table: "OfferCopies");
    }
}
