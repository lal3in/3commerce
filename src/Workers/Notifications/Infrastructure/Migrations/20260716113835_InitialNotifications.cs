using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Workers.Notifications.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialNotifications : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "notifications");

        migrationBuilder.CreateTable(
            name: "deliveries",
            schema: "notifications",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Recipient = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_deliveries", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_deliveries_OccurredAt",
            schema: "notifications",
            table: "deliveries",
            column: "OccurredAt");

        migrationBuilder.CreateIndex(
            name: "IX_deliveries_Status",
            schema: "notifications",
            table: "deliveries",
            column: "Status");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "deliveries",
            schema: "notifications");
    }
}
