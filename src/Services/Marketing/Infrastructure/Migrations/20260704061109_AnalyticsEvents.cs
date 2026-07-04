using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Marketing.Infrastructure.Migrations;

/// <inheritdoc />
public partial class AnalyticsEvents : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AnalyticsEvents",
            schema: "marketing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                VisitorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                SessionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                AnalyticsConsent = table.Column<bool>(type: "boolean", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                EventId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                ClientIpCoarse = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AnalyticsEvents", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsEvents_TenantId_EventId",
            schema: "marketing",
            table: "AnalyticsEvents",
            columns: new[] { "TenantId", "EventId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AnalyticsEvents_TenantId_ReceivedAt",
            schema: "marketing",
            table: "AnalyticsEvents",
            columns: new[] { "TenantId", "ReceivedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AnalyticsEvents",
            schema: "marketing");
    }
}
