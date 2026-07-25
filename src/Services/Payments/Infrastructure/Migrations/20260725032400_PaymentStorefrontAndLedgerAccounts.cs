using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PaymentStorefrontAndLedgerAccounts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "StorefrontId",
            schema: "payments",
            table: "Payments",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "StorefrontLedgerAccounts",
            schema: "payments",
            columns: table => new
            {
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: false),
                ReceivableAccountCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                RevenueAccountCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                TaxAccountCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorefrontLedgerAccounts", x => x.StorefrontId);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "StorefrontLedgerAccounts",
            schema: "payments");

        migrationBuilder.DropColumn(
            name: "StorefrontId",
            schema: "payments",
            table: "Payments");
    }
}
