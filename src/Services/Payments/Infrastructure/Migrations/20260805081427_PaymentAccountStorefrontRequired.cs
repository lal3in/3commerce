using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PaymentAccountStorefrontRequired : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PaymentAccounts_TenantId_IsDefaultForTenant",
            schema: "payments",
            table: "PaymentAccounts");

        migrationBuilder.DropIndex(
            name: "IX_PaymentAccounts_TenantId_StorefrontId",
            schema: "payments",
            table: "PaymentAccounts");

        migrationBuilder.RenameColumn(
            name: "IsDefaultForTenant",
            schema: "payments",
            table: "PaymentAccounts",
            newName: "IsDefaultForStorefront");

        // Payment accounts are now per-storefront only (ADR-0042): drop the deprecated tenant-level
        // rows (StorefrontId null) rather than coercing them to an invalid empty storefront id.
        migrationBuilder.Sql("DELETE FROM payments.\"PaymentAccounts\" WHERE \"StorefrontId\" IS NULL;");

        migrationBuilder.AlterColumn<Guid>(
            name: "StorefrontId",
            schema: "payments",
            table: "PaymentAccounts",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_PaymentAccounts_TenantId_StorefrontId_IsDefaultForStorefront",
            schema: "payments",
            table: "PaymentAccounts",
            columns: new[] { "TenantId", "StorefrontId", "IsDefaultForStorefront" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PaymentAccounts_TenantId_StorefrontId_IsDefaultForStorefront",
            schema: "payments",
            table: "PaymentAccounts");

        migrationBuilder.RenameColumn(
            name: "IsDefaultForStorefront",
            schema: "payments",
            table: "PaymentAccounts",
            newName: "IsDefaultForTenant");

        migrationBuilder.AlterColumn<Guid>(
            name: "StorefrontId",
            schema: "payments",
            table: "PaymentAccounts",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");

        migrationBuilder.CreateIndex(
            name: "IX_PaymentAccounts_TenantId_IsDefaultForTenant",
            schema: "payments",
            table: "PaymentAccounts",
            columns: new[] { "TenantId", "IsDefaultForTenant" });

        migrationBuilder.CreateIndex(
            name: "IX_PaymentAccounts_TenantId_StorefrontId",
            schema: "payments",
            table: "PaymentAccounts",
            columns: new[] { "TenantId", "StorefrontId" });
    }
}
