using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Marketing.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PublishingContent : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Contents",
            schema: "marketing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                DraftVersion = table.Column<int>(type: "integer", nullable: false),
                PublishedVersion = table.Column<int>(type: "integer", nullable: true),
                ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Contents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "JobRuns",
            schema: "marketing",
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

        migrationBuilder.CreateTable(
            name: "ContentVersions",
            schema: "marketing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ContentRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                Version = table.Column<int>(type: "integer", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ContentVersions", x => x.Id);
                table.ForeignKey(
                    name: "FK_ContentVersions_Contents_ContentRecordId",
                    column: x => x.ContentRecordId,
                    principalSchema: "marketing",
                    principalTable: "Contents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Contents_Status_ScheduledAt",
            schema: "marketing",
            table: "Contents",
            columns: new[] { "Status", "ScheduledAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Contents_TenantId_Key",
            schema: "marketing",
            table: "Contents",
            columns: new[] { "TenantId", "Key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ContentVersions_ContentRecordId_Version",
            schema: "marketing",
            table: "ContentVersions",
            columns: new[] { "ContentRecordId", "Version" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_JobRuns_JobName_StartedAt",
            schema: "marketing",
            table: "JobRuns",
            columns: new[] { "JobName", "StartedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ContentVersions",
            schema: "marketing");

        migrationBuilder.DropTable(
            name: "JobRuns",
            schema: "marketing");

        migrationBuilder.DropTable(
            name: "Contents",
            schema: "marketing");
    }
}
