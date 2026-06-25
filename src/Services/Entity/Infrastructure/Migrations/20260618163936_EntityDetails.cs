using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Entity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class EntityDetails : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "EntityAddresses",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Purpose = table.Column<int>(type: "integer", nullable: false),
                Version = table.Column<int>(type: "integer", nullable: false),
                Line1 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Line2 = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                City = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Region = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                Postcode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EntityAddresses", x => x.Id);
                table.ForeignKey(
                    name: "FK_EntityAddresses_Entities_EntityId",
                    column: x => x.EntityId,
                    principalSchema: "entity",
                    principalTable: "Entities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "EntityContactMethods",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Purpose = table.Column<int>(type: "integer", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                Value = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                VerificationStatus = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EntityContactMethods", x => x.Id);
                table.ForeignKey(
                    name: "FK_EntityContactMethods_Entities_EntityId",
                    column: x => x.EntityId,
                    principalSchema: "entity",
                    principalTable: "Entities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "EntityIdentifiers",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Type = table.Column<int>(type: "integer", nullable: false),
                Value = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                VerificationStatus = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EntityIdentifiers", x => x.Id);
                table.ForeignKey(
                    name: "FK_EntityIdentifiers_Entities_EntityId",
                    column: x => x.EntityId,
                    principalSchema: "entity",
                    principalTable: "Entities",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "EntityRelationships",
            schema: "entity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                FromEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                ToEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                Type = table.Column<int>(type: "integer", nullable: false),
                EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                EffectiveTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("PK_EntityRelationships", x => x.Id));

        migrationBuilder.CreateIndex("IX_EntityAddresses_EntityId_Purpose_IsCurrent", "EntityAddresses", new[] { "EntityId", "Purpose", "IsCurrent" }, schema: "entity");
        migrationBuilder.CreateIndex("IX_EntityAddresses_EntityId_Purpose_Version", "EntityAddresses", new[] { "EntityId", "Purpose", "Version" }, unique: true, schema: "entity");
        migrationBuilder.CreateIndex("IX_EntityContactMethods_EntityId_Purpose_Kind", "EntityContactMethods", new[] { "EntityId", "Purpose", "Kind" }, schema: "entity");
        migrationBuilder.CreateIndex("IX_EntityContactMethods_Kind_Value", "EntityContactMethods", new[] { "Kind", "Value" }, schema: "entity");
        migrationBuilder.CreateIndex("IX_EntityIdentifiers_EntityId_Type", "EntityIdentifiers", new[] { "EntityId", "Type" }, schema: "entity");
        migrationBuilder.CreateIndex("IX_EntityIdentifiers_Type_Value", "EntityIdentifiers", new[] { "Type", "Value" }, unique: true, schema: "entity");
        migrationBuilder.CreateIndex("IX_EntityRelationships_TenantId_FromEntityId_Type_EffectiveTo", "EntityRelationships", new[] { "TenantId", "FromEntityId", "Type", "EffectiveTo" }, schema: "entity");
        migrationBuilder.CreateIndex("IX_EntityRelationships_TenantId_ToEntityId_Type_EffectiveTo", "EntityRelationships", new[] { "TenantId", "ToEntityId", "Type", "EffectiveTo" }, schema: "entity");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "EntityAddresses", schema: "entity");
        migrationBuilder.DropTable(name: "EntityContactMethods", schema: "entity");
        migrationBuilder.DropTable(name: "EntityIdentifiers", schema: "entity");
        migrationBuilder.DropTable(name: "EntityRelationships", schema: "entity");
    }
}
