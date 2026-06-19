using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SupplierPayables : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PayoutInstructions",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SupplierEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                BankAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                Cadence = table.Column<int>(type: "integer", nullable: false),
                Active = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_PayoutInstructions", x => x.Id));

        migrationBuilder.CreateTable(
            name: "SupplierBankAccounts",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SupplierEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                AccountName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                BankCountry = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                RoutingNumberMasked = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                AccountNumberMasked = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                AccountTokenRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                State = table.Column<int>(type: "integer", nullable: false),
                ApprovalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_SupplierBankAccounts", x => x.Id));

        migrationBuilder.CreateTable(
            name: "SupplierPayablePolicies",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SupplierEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                CommissionBps = table.Column<int>(type: "integer", nullable: false),
                Cadence = table.Column<int>(type: "integer", nullable: false),
                Active = table.Column<bool>(type: "boolean", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_SupplierPayablePolicies", x => x.Id));

        migrationBuilder.CreateTable(
            name: "SupplierPayables",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SupplierEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                GrossMinor = table.Column<long>(type: "bigint", nullable: false),
                CommissionMinor = table.Column<long>(type: "bigint", nullable: false),
                NetPayableMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_SupplierPayables", x => x.Id));

        migrationBuilder.CreateIndex("IX_PayoutInstructions_TenantId_SupplierEntityId_Active", "PayoutInstructions", new[] { "TenantId", "SupplierEntityId", "Active" }, schema: "payments");
        migrationBuilder.CreateIndex("IX_SupplierBankAccounts_TenantId_SupplierEntityId_State", "SupplierBankAccounts", new[] { "TenantId", "SupplierEntityId", "State" }, schema: "payments");
        migrationBuilder.CreateIndex("IX_SupplierPayablePolicies_TenantId_SupplierEntityId_Active", "SupplierPayablePolicies", new[] { "TenantId", "SupplierEntityId", "Active" }, schema: "payments");
        migrationBuilder.CreateIndex("IX_SupplierPayables_TenantId_SupplierEntityId_OrderId", "SupplierPayables", new[] { "TenantId", "SupplierEntityId", "OrderId" }, schema: "payments");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PayoutInstructions", schema: "payments");
        migrationBuilder.DropTable(name: "SupplierBankAccounts", schema: "payments");
        migrationBuilder.DropTable(name: "SupplierPayablePolicies", schema: "payments");
        migrationBuilder.DropTable(name: "SupplierPayables", schema: "payments");
    }
}
