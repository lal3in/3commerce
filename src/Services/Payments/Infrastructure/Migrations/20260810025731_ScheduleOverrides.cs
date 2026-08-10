using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class ScheduleOverrides : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ScheduleOverrides",
            schema: "payments",
            columns: table => new
            {
                JobName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Cron = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Paused = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ScheduleOverrides", x => x.JobName);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ScheduleOverrides",
            schema: "payments");
    }
}
