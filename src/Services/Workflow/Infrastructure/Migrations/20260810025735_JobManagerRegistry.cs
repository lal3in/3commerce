using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Workflow.Infrastructure.Migrations;

/// <inheritdoc />
public partial class JobManagerRegistry : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Service",
            schema: "workflow",
            table: "Runs",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "ScheduledJobs",
            schema: "workflow",
            columns: table => new
            {
                Service = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Cron = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Paused = table.Column<bool>(type: "boolean", nullable: false),
                NextFireUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ScheduledJobs", x => new { x.Service, x.Name });
            });

        migrationBuilder.CreateTable(
            name: "ScheduleOverrides",
            schema: "workflow",
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
            name: "ScheduledJobs",
            schema: "workflow");

        migrationBuilder.DropTable(
            name: "ScheduleOverrides",
            schema: "workflow");

        migrationBuilder.DropColumn(
            name: "Service",
            schema: "workflow",
            table: "Runs");
    }
}
