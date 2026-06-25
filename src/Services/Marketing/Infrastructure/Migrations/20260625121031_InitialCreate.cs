using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Marketing.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "marketing");

        migrationBuilder.CreateTable(
            name: "Campaigns",
            schema: "marketing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Cid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Campaigns", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ShortLinks",
            schema: "marketing",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Destination = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                Cid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ClickCount = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ShortLinks", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Campaigns_TenantId_Cid",
            schema: "marketing",
            table: "Campaigns",
            columns: new[] { "TenantId", "Cid" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ShortLinks_TenantId_Code",
            schema: "marketing",
            table: "ShortLinks",
            columns: new[] { "TenantId", "Code" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Campaigns",
            schema: "marketing");

        migrationBuilder.DropTable(
            name: "ShortLinks",
            schema: "marketing");
    }
}
