using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class UsageChargeDunning : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UsageChargeAttempts",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                UsageChargeId = table.Column<Guid>(type: "uuid", nullable: false),
                Attempt = table.Column<int>(type: "integer", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(170)", maxLength: 170, nullable: false),
                Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                PaymentIntentId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                Outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UsageChargeAttempts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "UsageCharges",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: true),
                CustomerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                Meter = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Quantity = table.Column<long>(type: "bigint", nullable: false),
                UnitPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                PriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Reference = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                DunningStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                LastFailureReason = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UsageCharges", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UsageChargeAttempts_IdempotencyKey",
            schema: "payments",
            table: "UsageChargeAttempts",
            column: "IdempotencyKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UsageChargeAttempts_PaymentIntentId",
            schema: "payments",
            table: "UsageChargeAttempts",
            column: "PaymentIntentId");

        migrationBuilder.CreateIndex(
            name: "IX_UsageChargeAttempts_UsageChargeId_Attempt",
            schema: "payments",
            table: "UsageChargeAttempts",
            columns: new[] { "UsageChargeId", "Attempt" });

        migrationBuilder.CreateIndex(
            name: "IX_UsageCharges_Reference",
            schema: "payments",
            table: "UsageCharges",
            column: "Reference",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UsageCharges_Status_DunningStartedAt",
            schema: "payments",
            table: "UsageCharges",
            columns: new[] { "Status", "DunningStartedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "UsageChargeAttempts",
            schema: "payments");

        migrationBuilder.DropTable(
            name: "UsageCharges",
            schema: "payments");
    }
}
