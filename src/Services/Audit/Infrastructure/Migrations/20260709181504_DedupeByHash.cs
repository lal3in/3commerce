using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Audit.Infrastructure.Migrations;

/// <inheritdoc />
public partial class DedupeByHash : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AuditEntries_TenantId_Sequence",
            schema: "audit",
            table: "AuditEntries");

        migrationBuilder.CreateIndex(
            name: "IX_AuditEntries_TenantId_Hash",
            schema: "audit",
            table: "AuditEntries",
            columns: new[] { "TenantId", "Hash" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AuditEntries_TenantId_Hash",
            schema: "audit",
            table: "AuditEntries");

        migrationBuilder.CreateIndex(
            name: "IX_AuditEntries_TenantId_Sequence",
            schema: "audit",
            table: "AuditEntries",
            columns: new[] { "TenantId", "Sequence" },
            unique: true);
    }
}
