using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Fulfillment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class UsageOverageBilling : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "BilledOverageQuantity",
            schema: "fulfillment",
            table: "UsageBalances",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<string>(
            name: "Currency",
            schema: "fulfillment",
            table: "UsageBalances",
            type: "character varying(3)",
            maxLength: 3,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<bool>(
            name: "OverageAllowed",
            schema: "fulfillment",
            table: "UsageBalances",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<long>(
            name: "OverageUnitPriceMinor",
            schema: "fulfillment",
            table: "UsageBalances",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "BilledOverageQuantity",
            schema: "fulfillment",
            table: "UsageBalances");

        migrationBuilder.DropColumn(
            name: "Currency",
            schema: "fulfillment",
            table: "UsageBalances");

        migrationBuilder.DropColumn(
            name: "OverageAllowed",
            schema: "fulfillment",
            table: "UsageBalances");

        migrationBuilder.DropColumn(
            name: "OverageUnitPriceMinor",
            schema: "fulfillment",
            table: "UsageBalances");
    }
}
