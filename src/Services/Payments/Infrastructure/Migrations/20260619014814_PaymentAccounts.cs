using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PaymentAccounts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PaymentAccounts",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: true),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Mode = table.Column<int>(type: "integer", nullable: false),
                State = table.Column<int>(type: "integer", nullable: false),
                IsDefaultForTenant = table.Column<bool>(type: "boolean", nullable: false),
                ExternalAccountRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_PaymentAccounts", x => x.Id));

        migrationBuilder.CreateIndex("IX_PaymentAccounts_TenantId_IsDefaultForTenant", "PaymentAccounts", new[] { "TenantId", "IsDefaultForTenant" }, schema: "payments");
        migrationBuilder.CreateIndex("IX_PaymentAccounts_TenantId_StorefrontId", "PaymentAccounts", new[] { "TenantId", "StorefrontId" }, schema: "payments");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PaymentAccounts", schema: "payments");
    }
}
