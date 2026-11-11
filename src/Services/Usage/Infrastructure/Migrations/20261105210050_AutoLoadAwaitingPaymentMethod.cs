using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Usage.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AutoLoadAwaitingPaymentMethod : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "AutoLoadAwaitingPaymentMethodSince",
            schema: "usage",
            table: "UsageBalances",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PaymentMethodReadyAt",
            schema: "usage",
            table: "UsageBalances",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AutoLoadAwaitingPaymentMethodSince",
            schema: "usage",
            table: "UsageBalances");

        migrationBuilder.DropColumn(
            name: "PaymentMethodReadyAt",
            schema: "usage",
            table: "UsageBalances");
    }
}
