using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Identity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class LinkUsersToPrincipals : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Users_Email",
            schema: "identity",
            table: "Users");

        migrationBuilder.AddColumn<Guid>(
            name: "PrincipalId",
            schema: "identity",
            table: "Users",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "TenantId",
            schema: "identity",
            table: "Users",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Users_PrincipalId",
            schema: "identity",
            table: "Users",
            column: "PrincipalId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Users_TenantId_Email",
            schema: "identity",
            table: "Users",
            columns: new[] { "TenantId", "Email" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_Users_Principals_PrincipalId",
            schema: "identity",
            table: "Users",
            column: "PrincipalId",
            principalSchema: "identity",
            principalTable: "Principals",
            principalColumn: "Id");

        migrationBuilder.AddForeignKey(
            name: "FK_Users_Tenants_TenantId",
            schema: "identity",
            table: "Users",
            column: "TenantId",
            principalSchema: "identity",
            principalTable: "Tenants",
            principalColumn: "Id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Users_Principals_PrincipalId",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropForeignKey(
            name: "FK_Users_Tenants_TenantId",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropIndex(
            name: "IX_Users_PrincipalId",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropIndex(
            name: "IX_Users_TenantId_Email",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "PrincipalId",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "TenantId",
            schema: "identity",
            table: "Users");

        migrationBuilder.CreateIndex(
            name: "IX_Users_Email",
            schema: "identity",
            table: "Users",
            column: "Email",
            unique: true);
    }
}
