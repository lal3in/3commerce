using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class DisputeStatusAndTerminalStates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "DisputeStatus",
            schema: "payments",
            table: "Payments",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "ProviderDisputeId",
            schema: "payments",
            table: "Payments",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "VoidPayments",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OriginalPaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                PaymentIntentId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                ProviderDisputeId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Reason = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VoidPayments", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_VoidPayments_OrderId",
            schema: "payments",
            table: "VoidPayments",
            column: "OrderId");

        migrationBuilder.CreateIndex(
            name: "IX_VoidPayments_OriginalPaymentId",
            schema: "payments",
            table: "VoidPayments",
            column: "OriginalPaymentId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "VoidPayments",
            schema: "payments");

        migrationBuilder.DropColumn(
            name: "DisputeStatus",
            schema: "payments",
            table: "Payments");

        migrationBuilder.DropColumn(
            name: "ProviderDisputeId",
            schema: "payments",
            table: "Payments");
    }
}
