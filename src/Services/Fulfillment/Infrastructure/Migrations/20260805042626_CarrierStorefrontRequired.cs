using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Fulfillment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class CarrierStorefrontRequired : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Carriers are now per-storefront only (ADR-0042): drop the deprecated tenant-level rows
        // (StorefrontId null) rather than coercing them to an invalid empty storefront id.
        migrationBuilder.Sql("DELETE FROM fulfillment.\"CarrierIntegrations\" WHERE \"StorefrontId\" IS NULL;");

        migrationBuilder.AlterColumn<Guid>(
            name: "StorefrontId",
            schema: "fulfillment",
            table: "CarrierIntegrations",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<Guid>(
            name: "StorefrontId",
            schema: "fulfillment",
            table: "CarrierIntegrations",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");
    }
}
