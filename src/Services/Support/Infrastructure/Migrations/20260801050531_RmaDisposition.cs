using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Support.Infrastructure.Migrations;

/// <inheritdoc />
public partial class RmaDisposition : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ReturnReceivedAt",
            schema: "support",
            table: "Rmas",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "RmaDispositions",
            schema: "support",
            columns: table => new
            {
                RmaId = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                StorageReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                Comments = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RmaDispositions", x => x.RmaId);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "RmaDispositions",
            schema: "support");

        migrationBuilder.DropColumn(
            name: "ReturnReceivedAt",
            schema: "support",
            table: "Rmas");
    }
}
