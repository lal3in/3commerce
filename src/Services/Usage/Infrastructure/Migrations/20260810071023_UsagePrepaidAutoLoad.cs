using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Usage.Infrastructure.Migrations;

/// <inheritdoc />
public partial class UsagePrepaidAutoLoad : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "AutoLoadCount",
            schema: "usage",
            table: "UsageBalances",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<bool>(
            name: "AutoLoadEnabled",
            schema: "usage",
            table: "UsageBalances",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<long>(
            name: "AutoLoadReloadQuantity",
            schema: "usage",
            table: "UsageBalances",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "AutoLoadThresholdQuantity",
            schema: "usage",
            table: "UsageBalances",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<long>(
            name: "PrepaidRemainingQuantity",
            schema: "usage",
            table: "UsageBalances",
            type: "bigint",
            nullable: false,
            defaultValue: 0L);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AutoLoadCount",
            schema: "usage",
            table: "UsageBalances");

        migrationBuilder.DropColumn(
            name: "AutoLoadEnabled",
            schema: "usage",
            table: "UsageBalances");

        migrationBuilder.DropColumn(
            name: "AutoLoadReloadQuantity",
            schema: "usage",
            table: "UsageBalances");

        migrationBuilder.DropColumn(
            name: "AutoLoadThresholdQuantity",
            schema: "usage",
            table: "UsageBalances");

        migrationBuilder.DropColumn(
            name: "PrepaidRemainingQuantity",
            schema: "usage",
            table: "UsageBalances");
    }
}
