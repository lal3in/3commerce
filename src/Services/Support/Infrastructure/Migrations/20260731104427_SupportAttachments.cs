using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Support.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SupportAttachments : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Attachments",
            schema: "support",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Attachments", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Attachments_OwnerKind_OwnerId",
            schema: "support",
            table: "Attachments",
            columns: new[] { "OwnerKind", "OwnerId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Attachments",
            schema: "support");
    }
}
