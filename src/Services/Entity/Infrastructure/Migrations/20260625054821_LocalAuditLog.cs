using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class LocalAuditLog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuditEntries",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                ActorRole = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ResourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ResourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Summary = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                PrevHash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Hash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditEntries", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuditEntries_TenantId_Sequence",
            schema: "entity",
            table: "AuditEntries",
            columns: new[] { "TenantId", "Sequence" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AuditEntries",
            schema: "entity");
    }
}
