using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Catalog.Infrastructure.Migrations;

/// <inheritdoc />
public partial class StorefrontLifecycle : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Storefronts",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                State = table.Column<int>(type: "integer", nullable: false),
                Visibility = table.Column<int>(type: "integer", nullable: false),
                AccessPasswordHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_Storefronts", x => x.Id));

        migrationBuilder.CreateTable(
            name: "StorefrontDomains",
            schema: "catalog",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: false),
                Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                Canonical = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorefrontDomains", x => x.Id);
                table.ForeignKey(
                    name: "FK_StorefrontDomains_Storefronts_StorefrontId",
                    column: x => x.StorefrontId,
                    principalSchema: "catalog",
                    principalTable: "Storefronts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_StorefrontDomains_Host", "StorefrontDomains", "Host", schema: "catalog", unique: true);
        migrationBuilder.CreateIndex("IX_StorefrontDomains_StorefrontId_Canonical", "StorefrontDomains", new[] { "StorefrontId", "Canonical" }, schema: "catalog");
        migrationBuilder.CreateIndex("IX_Storefronts_TenantId_Name", "Storefronts", new[] { "TenantId", "Name" }, schema: "catalog", unique: true);
        migrationBuilder.CreateIndex("IX_Storefronts_TenantId_State", "Storefronts", new[] { "TenantId", "State" }, schema: "catalog");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "StorefrontDomains", schema: "catalog");
        migrationBuilder.DropTable(name: "Storefronts", schema: "catalog");
    }
}
