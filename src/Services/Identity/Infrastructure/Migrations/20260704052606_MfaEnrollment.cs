using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Identity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class MfaEnrollment : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "MfaPolicy",
            schema: "identity",
            table: "Tenants",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<bool>(
            name: "MfaPending",
            schema: "identity",
            table: "Sessions",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "StrongAuthAt",
            schema: "identity",
            table: "Sessions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "MfaEnrollments",
            schema: "identity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                SecretBase32 = table.Column<string>(type: "text", nullable: false),
                ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RecoveryCodeHashes = table.Column<List<string>>(type: "text[]", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MfaEnrollments", x => x.Id);
                table.ForeignKey(
                    name: "FK_MfaEnrollments_Users_UserId",
                    column: x => x.UserId,
                    principalSchema: "identity",
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MfaEnrollments_UserId",
            schema: "identity",
            table: "MfaEnrollments",
            column: "UserId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MfaEnrollments",
            schema: "identity");

        migrationBuilder.DropColumn(
            name: "MfaPolicy",
            schema: "identity",
            table: "Tenants");

        migrationBuilder.DropColumn(
            name: "MfaPending",
            schema: "identity",
            table: "Sessions");

        migrationBuilder.DropColumn(
            name: "StrongAuthAt",
            schema: "identity",
            table: "Sessions");
    }
}
