using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Fulfillment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class CarrierIntegrations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CarrierIntegrations",
            schema: "fulfillment",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: true),
                Carrier = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                CredentialRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CarrierIntegrations", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CarrierIntegrations_TenantId_StorefrontId",
            schema: "fulfillment",
            table: "CarrierIntegrations",
            columns: new[] { "TenantId", "StorefrontId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CarrierIntegrations",
            schema: "fulfillment");
    }
}
