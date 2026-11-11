using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class UsageAutoLoadSavedCard : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProviderCustomerId",
            schema: "payments",
            table: "UsageCharges",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProviderPaymentMethodId",
            schema: "payments",
            table: "UsageCharges",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Email",
            schema: "payments",
            table: "PaymentCustomers",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_PaymentCustomers_TenantId_Email",
            schema: "payments",
            table: "PaymentCustomers",
            columns: new[] { "TenantId", "Email" });

        // Backfill (jobmgr_3_settlement): a vault whose customer bought a subscription with a card saved in it is named by
        // that subscription's email — only when exactly one email charged the vault in its tenant (never a guess). Every
        // other vault learns its email the next time the customer manages or lists their cards.
        migrationBuilder.Sql("""
            UPDATE payments."PaymentCustomers" c
            SET "Email" = s."Email"
            FROM (
                SELECT "TenantId", "ProviderCustomerId", MIN("CustomerEmail") AS "Email"
                FROM payments."Subscriptions"
                WHERE "ProviderCustomerId" IS NOT NULL AND "CustomerEmail" <> ''
                GROUP BY "TenantId", "ProviderCustomerId"
                HAVING COUNT(DISTINCT "CustomerEmail") = 1
            ) s
            WHERE c."Email" IS NULL AND c."TenantId" = s."TenantId" AND c."ProviderCustomerId" = s."ProviderCustomerId";
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PaymentCustomers_TenantId_Email",
            schema: "payments",
            table: "PaymentCustomers");

        migrationBuilder.DropColumn(
            name: "ProviderCustomerId",
            schema: "payments",
            table: "UsageCharges");

        migrationBuilder.DropColumn(
            name: "ProviderPaymentMethodId",
            schema: "payments",
            table: "UsageCharges");

        migrationBuilder.DropColumn(
            name: "Email",
            schema: "payments",
            table: "PaymentCustomers");
    }
}
