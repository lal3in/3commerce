using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class DuplicateWarnings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_EntityIdentifiers_Type_Value",
            schema: "entity",
            table: "EntityIdentifiers");

        migrationBuilder.CreateTable(
            name: "DuplicateWarnings",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                CandidateEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                ExistingEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                MatchedValue = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                OverrideReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                OverriddenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_DuplicateWarnings", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_EntityIdentifiers_Type_Value",
            schema: "entity",
            table: "EntityIdentifiers",
            columns: new[] { "Type", "Value" });

        migrationBuilder.CreateIndex(
            name: "IX_DuplicateWarnings_TenantId_CandidateEntityId_Status",
            schema: "entity",
            table: "DuplicateWarnings",
            columns: new[] { "TenantId", "CandidateEntityId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_DuplicateWarnings_TenantId_Kind_MatchedValue",
            schema: "entity",
            table: "DuplicateWarnings",
            columns: new[] { "TenantId", "Kind", "MatchedValue" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "DuplicateWarnings", schema: "entity");

        migrationBuilder.DropIndex(
            name: "IX_EntityIdentifiers_Type_Value",
            schema: "entity",
            table: "EntityIdentifiers");

        migrationBuilder.CreateIndex(
            name: "IX_EntityIdentifiers_Type_Value",
            schema: "entity",
            table: "EntityIdentifiers",
            columns: new[] { "Type", "Value" },
            unique: true);
    }
}
