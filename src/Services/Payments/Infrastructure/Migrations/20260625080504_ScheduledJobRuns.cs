using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class ScheduledJobRuns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "JobRuns",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                JobName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JobRuns", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_JobRuns_JobName_StartedAt",
            schema: "payments",
            table: "JobRuns",
            columns: new[] { "JobName", "StartedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "JobRuns",
            schema: "payments");
    }
}
