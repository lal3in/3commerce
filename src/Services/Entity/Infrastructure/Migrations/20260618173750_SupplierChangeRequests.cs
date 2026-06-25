using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SupplierChangeRequests : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SupplierChangeRequests",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Type = table.Column<int>(type: "integer", nullable: false),
                Summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                RequestedByPrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DecidedByPrincipalId = table.Column<Guid>(type: "uuid", nullable: true),
                DecisionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SupplierChangeRequests", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SupplierChangeRequests_TenantId_EntityId",
            schema: "entity",
            table: "SupplierChangeRequests",
            columns: new[] { "TenantId", "EntityId" });

        migrationBuilder.CreateIndex(
            name: "IX_SupplierChangeRequests_TenantId_Status",
            schema: "entity",
            table: "SupplierChangeRequests",
            columns: new[] { "TenantId", "Status" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SupplierChangeRequests",
            schema: "entity");
    }
}
