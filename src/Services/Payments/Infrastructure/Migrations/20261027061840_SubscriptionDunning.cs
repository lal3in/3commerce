using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SubscriptionDunning : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AttemptCount",
            schema: "payments",
            table: "Subscriptions",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DunningStartedAt",
            schema: "payments",
            table: "Subscriptions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastAttemptAt",
            schema: "payments",
            table: "Subscriptions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LastFailureReason",
            schema: "payments",
            table: "Subscriptions",
            type: "character varying(24)",
            maxLength: 24,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "SubscriptionChargeAttempts",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Attempt = table.Column<int>(type: "integer", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
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
                table.PrimaryKey("PK_SubscriptionChargeAttempts", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Subscriptions_StorefrontId_Status_DunningStartedAt",
            schema: "payments",
            table: "Subscriptions",
            columns: new[] { "StorefrontId", "Status", "DunningStartedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_SubscriptionChargeAttempts_IdempotencyKey",
            schema: "payments",
            table: "SubscriptionChargeAttempts",
            column: "IdempotencyKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SubscriptionChargeAttempts_PaymentIntentId",
            schema: "payments",
            table: "SubscriptionChargeAttempts",
            column: "PaymentIntentId");

        migrationBuilder.CreateIndex(
            name: "IX_SubscriptionChargeAttempts_SubscriptionId_PeriodEnd",
            schema: "payments",
            table: "SubscriptionChargeAttempts",
            columns: new[] { "SubscriptionId", "PeriodEnd" });

        // Subscriptions already past due before dunning existed get the full schedule from the deployment on
        // (retries on days 1, 3 and 7) rather than an immediate final retry dated from an old failure.
        migrationBuilder.Sql(
            """UPDATE payments."Subscriptions" SET "DunningStartedAt" = now(), "LastFailureReason" = 'Error' WHERE "Status" = 'PastDue';""");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SubscriptionChargeAttempts",
            schema: "payments");

        migrationBuilder.DropIndex(
            name: "IX_Subscriptions_StorefrontId_Status_DunningStartedAt",
            schema: "payments",
            table: "Subscriptions");

        migrationBuilder.DropColumn(
            name: "AttemptCount",
            schema: "payments",
            table: "Subscriptions");

        migrationBuilder.DropColumn(
            name: "DunningStartedAt",
            schema: "payments",
            table: "Subscriptions");

        migrationBuilder.DropColumn(
            name: "LastAttemptAt",
            schema: "payments",
            table: "Subscriptions");

        migrationBuilder.DropColumn(
            name: "LastFailureReason",
            schema: "payments",
            table: "Subscriptions");
    }
}
