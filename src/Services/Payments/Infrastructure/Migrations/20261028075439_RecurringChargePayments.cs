using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class RecurringChargePayments : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Payments_OrderId",
            schema: "payments",
            table: "Payments");

        migrationBuilder.AddColumn<string>(
            name: "ChargeReference",
            schema: "payments",
            table: "Payments",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Kind",
            schema: "payments",
            table: "Payments",
            type: "integer",
            nullable: false,
            defaultValue: 1);

        migrationBuilder.AddColumn<Guid>(
            name: "SubscriptionId",
            schema: "payments",
            table: "Payments",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Payments_ChargeReference",
            schema: "payments",
            table: "Payments",
            column: "ChargeReference",
            unique: true,
            filter: "\"ChargeReference\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Payments_OrderId",
            schema: "payments",
            table: "Payments",
            column: "OrderId",
            unique: true,
            filter: "\"Kind\" = 1");

        migrationBuilder.CreateIndex(
            name: "IX_Payments_SubscriptionId",
            schema: "payments",
            table: "Payments",
            column: "SubscriptionId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Payments_ChargeReference",
            schema: "payments",
            table: "Payments");

        migrationBuilder.DropIndex(
            name: "IX_Payments_OrderId",
            schema: "payments",
            table: "Payments");

        migrationBuilder.DropIndex(
            name: "IX_Payments_SubscriptionId",
            schema: "payments",
            table: "Payments");

        migrationBuilder.DropColumn(
            name: "ChargeReference",
            schema: "payments",
            table: "Payments");

        migrationBuilder.DropColumn(
            name: "Kind",
            schema: "payments",
            table: "Payments");

        migrationBuilder.DropColumn(
            name: "SubscriptionId",
            schema: "payments",
            table: "Payments");

        migrationBuilder.CreateIndex(
            name: "IX_Payments_OrderId",
            schema: "payments",
            table: "Payments",
            column: "OrderId",
            unique: true);
    }
}
