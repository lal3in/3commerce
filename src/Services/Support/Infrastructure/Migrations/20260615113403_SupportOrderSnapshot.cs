using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Support.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SupportOrderSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderSnapshots",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    GrossMinor = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderSnapshots", x => x.OrderId);
                });

            migrationBuilder.CreateTable(
                name: "OrderSnapshotLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    UnitPriceMinor = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderSnapshotLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderSnapshotLines_OrderSnapshots_OrderId",
                        column: x => x.OrderId,
                        principalTable: "OrderSnapshots",
                        principalColumn: "OrderId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderSnapshotLines_OrderId",
                table: "OrderSnapshotLines",
                column: "OrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderSnapshotLines");

            migrationBuilder.DropTable(
                name: "OrderSnapshots");
        }
    }
}
