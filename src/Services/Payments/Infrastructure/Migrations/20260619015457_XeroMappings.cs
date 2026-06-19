using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class XeroMappings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "XeroAccountMappings",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Scope = table.Column<int>(type: "integer", nullable: false),
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: true),
                CategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                SupplierEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                LedgerAccountCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                XeroAccountCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Active = table.Column<bool>(type: "boolean", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_XeroAccountMappings", x => x.Id));

        migrationBuilder.CreateIndex("IX_XeroAccountMappings_TenantId_Scope_LedgerAccountCode", "XeroAccountMappings", new[] { "TenantId", "Scope", "LedgerAccountCode" }, schema: "payments");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "XeroAccountMappings", schema: "payments");
    }
}
