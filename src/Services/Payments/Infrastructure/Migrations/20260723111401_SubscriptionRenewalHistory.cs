using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SubscriptionRenewalHistory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SubscriptionRenewals",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SubscriptionRenewals", x => x.Id);
                table.ForeignKey(
                    name: "FK_SubscriptionRenewals_Subscriptions_SubscriptionId",
                    column: x => x.SubscriptionId,
                    principalSchema: "payments",
                    principalTable: "Subscriptions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SubscriptionRenewals_SubscriptionId",
            schema: "payments",
            table: "SubscriptionRenewals",
            column: "SubscriptionId");

        migrationBuilder.CreateIndex(
            name: "IX_SubscriptionRenewals_SubscriptionId_Sequence",
            schema: "payments",
            table: "SubscriptionRenewals",
            columns: new[] { "SubscriptionId", "Sequence" },
            unique: true);

        // Backfill a first-period (Sequence 1) row for every existing subscription so the live seeded
        // subscriptions show a history immediately. The period starts at signup (CreatedAt) and its end
        // is derived from the billing cadence — independent of any later renewal that advanced
        // CurrentPeriod*. Idempotent: INSERT ... WHERE NOT EXISTS skips subscriptions already backfilled.
        migrationBuilder.Sql(
            """
            INSERT INTO payments."SubscriptionRenewals"
                ("Id", "SubscriptionId", "Sequence", "PeriodStart", "PeriodEnd", "AmountMinor", "Currency", "RecordedAt")
            SELECT
                gen_random_uuid(),
                s."Id",
                1,
                s."CreatedAt",
                CASE s."BillingPeriod"
                    WHEN 'Monthly' THEN s."CreatedAt" + INTERVAL '1 month'
                    WHEN 'Yearly'  THEN s."CreatedAt" + INTERVAL '1 year'
                    ELSE s."CreatedAt"
                END,
                s."PriceMinor",
                s."Currency",
                s."CreatedAt"
            FROM payments."Subscriptions" s
            WHERE NOT EXISTS (
                SELECT 1 FROM payments."SubscriptionRenewals" r WHERE r."SubscriptionId" = s."Id"
            );
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SubscriptionRenewals",
            schema: "payments");
    }
}
