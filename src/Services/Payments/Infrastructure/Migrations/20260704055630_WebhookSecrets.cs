using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class WebhookSecrets : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WebhookSecrets",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Secret = table.Column<string>(type: "text", nullable: false),
                Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Active = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DeactivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WebhookSecrets", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WebhookSecrets_Provider_Active",
            schema: "payments",
            table: "WebhookSecrets",
            columns: new[] { "Provider", "Active" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "WebhookSecrets",
            schema: "payments");
    }
}
