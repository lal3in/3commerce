using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Support.Infrastructure.Migrations;

/// <inheritdoc />
public partial class RmaRequestRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RmaRequests",
            schema: "support",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RmaRequests", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "RmaRequestLines",
            schema: "support",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RmaId = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false),
                UnitPriceMinor = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RmaRequestLines", x => x.Id);
                table.ForeignKey(
                    name: "FK_RmaRequestLines_RmaRequests_RmaId",
                    column: x => x.RmaId,
                    principalSchema: "support",
                    principalTable: "RmaRequests",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RmaRequestLines_RmaId",
            schema: "support",
            table: "RmaRequestLines",
            column: "RmaId");

        migrationBuilder.CreateIndex(
            name: "IX_RmaRequests_OrderId",
            schema: "support",
            table: "RmaRequests",
            column: "OrderId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "RmaRequestLines",
            schema: "support");

        migrationBuilder.DropTable(
            name: "RmaRequests",
            schema: "support");
    }
}
