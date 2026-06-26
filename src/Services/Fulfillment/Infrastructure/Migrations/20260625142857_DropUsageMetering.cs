using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Fulfillment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class DropUsageMetering : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "UsageBalances",
            schema: "fulfillment");

        migrationBuilder.DropTable(
            name: "UsageRecords",
            schema: "fulfillment");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UsageBalances",
            schema: "fulfillment",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                BilledOverageQuantity = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                CustomerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                IncludedQuantity = table.Column<long>(type: "bigint", nullable: false),
                Meter = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                OverageAllowed = table.Column<bool>(type: "boolean", nullable: false),
                OverageUnitPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UsedQuantity = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UsageBalances", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "UsageRecords",
            schema: "fulfillment",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                BalanceId = table.Column<Guid>(type: "uuid", nullable: false),
                CustomerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Meter = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Quantity = table.Column<long>(type: "bigint", nullable: false),
                ReferenceId = table.Column<string>(type: "text", nullable: true),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UsageRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UsageBalances_TenantId_CustomerEmail_Meter",
            schema: "fulfillment",
            table: "UsageBalances",
            columns: new[] { "TenantId", "CustomerEmail", "Meter" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UsageRecords_BalanceId",
            schema: "fulfillment",
            table: "UsageRecords",
            column: "BalanceId");

        migrationBuilder.CreateIndex(
            name: "IX_UsageRecords_TenantId_ReferenceId",
            schema: "fulfillment",
            table: "UsageRecords",
            columns: new[] { "TenantId", "ReferenceId" });
    }
}
