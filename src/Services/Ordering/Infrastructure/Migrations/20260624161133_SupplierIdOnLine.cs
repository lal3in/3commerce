using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SupplierIdOnLine : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "SupplierId",
            schema: "ordering",
            table: "OrderLines",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "SupplierId",
            schema: "ordering",
            table: "OfferCopies",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.AddColumn<Guid>(
            name: "SupplierId",
            schema: "ordering",
            table: "CheckoutAttemptLines",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SupplierId",
            schema: "ordering",
            table: "OrderLines");

        migrationBuilder.DropColumn(
            name: "SupplierId",
            schema: "ordering",
            table: "OfferCopies");

        migrationBuilder.DropColumn(
            name: "SupplierId",
            schema: "ordering",
            table: "CheckoutAttemptLines");
    }
}
