using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Fulfillment.Infrastructure.Migrations;

/// <inheritdoc />
public partial class ShipmentPackages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "TenantId",
            schema: "fulfillment",
            table: "Shipments",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.CreateTable(
            name: "Packages",
            schema: "fulfillment",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                WeightGrams = table.Column<int>(type: "integer", nullable: false),
                LengthMm = table.Column<int>(type: "integer", nullable: false),
                WidthMm = table.Column<int>(type: "integer", nullable: false),
                HeightMm = table.Column<int>(type: "integer", nullable: false),
                Carrier = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                TrackingNumber = table.Column<string>(type: "text", nullable: true),
                LabelUrl = table.Column<string>(type: "text", nullable: true),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Packages", x => x.Id);
                table.ForeignKey(
                    name: "FK_Packages_Shipments_ShipmentId",
                    column: x => x.ShipmentId,
                    principalSchema: "fulfillment",
                    principalTable: "Shipments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Packages_ShipmentId",
            schema: "fulfillment",
            table: "Packages",
            column: "ShipmentId");

        migrationBuilder.CreateIndex(
            name: "IX_Packages_TenantId_TrackingNumber",
            schema: "fulfillment",
            table: "Packages",
            columns: new[] { "TenantId", "TrackingNumber" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Packages",
            schema: "fulfillment");

        migrationBuilder.DropColumn(
            name: "TenantId",
            schema: "fulfillment",
            table: "Shipments");
    }
}
