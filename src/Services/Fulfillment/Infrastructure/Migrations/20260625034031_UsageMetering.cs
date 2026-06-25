using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Fulfillment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class UsageMetering : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UsageBalances",
            schema: "fulfillment",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                CustomerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Meter = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                IncludedQuantity = table.Column<long>(type: "bigint", nullable: false),
                UsedQuantity = table.Column<long>(type: "bigint", nullable: false),
                PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                BalanceId = table.Column<Guid>(type: "uuid", nullable: false),
                CustomerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Meter = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Quantity = table.Column<long>(type: "bigint", nullable: false),
                ReferenceId = table.Column<string>(type: "text", nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "UsageBalances",
            schema: "fulfillment");

        migrationBuilder.DropTable(
            name: "UsageRecords",
            schema: "fulfillment");
    }
}
