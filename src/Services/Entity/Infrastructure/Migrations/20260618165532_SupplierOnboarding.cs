using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SupplierOnboarding : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SupplierOnboardings",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                State = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                SuspensionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_SupplierOnboardings", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_SupplierOnboardings_TenantId_EntityId",
            schema: "entity",
            table: "SupplierOnboardings",
            columns: new[] { "TenantId", "EntityId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SupplierOnboardings_TenantId_State",
            schema: "entity",
            table: "SupplierOnboardings",
            columns: new[] { "TenantId", "State" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SupplierOnboardings", schema: "entity");
    }
}
