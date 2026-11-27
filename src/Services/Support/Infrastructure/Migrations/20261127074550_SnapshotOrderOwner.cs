using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Support.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SnapshotOrderOwner : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "UserId",
            schema: "support",
            table: "OrderSnapshots",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_OrderSnapshots_UserId",
            schema: "support",
            table: "OrderSnapshots",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_OrderSnapshots_UserId",
            schema: "support",
            table: "OrderSnapshots");

        migrationBuilder.DropColumn(
            name: "UserId",
            schema: "support",
            table: "OrderSnapshots");
    }
}
