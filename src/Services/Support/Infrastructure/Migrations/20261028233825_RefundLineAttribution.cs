using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Support.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefundLineAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrderLineId",
                schema: "support",
                table: "RmaRequestLines",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrderLineId",
                schema: "support",
                table: "OrderSnapshotLines",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OrderLineId",
                schema: "support",
                table: "RmaRequestLines");

            migrationBuilder.DropColumn(
                name: "OrderLineId",
                schema: "support",
                table: "OrderSnapshotLines");
        }
    }
}
