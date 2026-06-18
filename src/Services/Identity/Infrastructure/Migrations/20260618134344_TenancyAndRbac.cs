using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Identity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class TenancyAndRbac : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Permissions",
            schema: "identity",
            columns: table => new
            {
                Key = table.Column<string>(type: "text", nullable: false),
                Description = table.Column<string>(type: "text", nullable: false),
                RiskLevel = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Permissions", x => x.Key);
            });

        migrationBuilder.CreateTable(
            name: "Principals",
            schema: "identity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Type = table.Column<int>(type: "integer", nullable: false),
                DisplayName = table.Column<string>(type: "text", nullable: true),
                IsPlatformAdmin = table.Column<bool>(type: "boolean", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Principals", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Roles",
            schema: "identity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                Key = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Description = table.Column<string>(type: "text", nullable: true),
                IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Roles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Tenants",
            schema: "identity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Slug = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                HomeRegion = table.Column<string>(type: "text", nullable: false),
                OwnerLegalEntityRef = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tenants", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "ServiceAccounts",
            schema: "identity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                Name = table.Column<string>(type: "text", nullable: false),
                ClientId = table.Column<string>(type: "text", nullable: false),
                SecretHash = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ServiceAccounts", x => x.Id);
                table.ForeignKey(
                    name: "FK_ServiceAccounts_Principals_PrincipalId",
                    column: x => x.PrincipalId,
                    principalSchema: "identity",
                    principalTable: "Principals",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RolePermissions",
            schema: "identity",
            columns: table => new
            {
                RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                PermissionKey = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RolePermissions", x => new { x.RoleId, x.PermissionKey });
                table.ForeignKey(
                    name: "FK_RolePermissions_Permissions_PermissionKey",
                    column: x => x.PermissionKey,
                    principalSchema: "identity",
                    principalTable: "Permissions",
                    principalColumn: "Key",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_RolePermissions_Roles_RoleId",
                    column: x => x.RoleId,
                    principalSchema: "identity",
                    principalTable: "Roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TenantMemberships",
            schema: "identity",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                IsTenantOwner = table.Column<bool>(type: "boolean", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TenantMemberships", x => x.Id);
                table.ForeignKey(
                    name: "FK_TenantMemberships_Principals_PrincipalId",
                    column: x => x.PrincipalId,
                    principalSchema: "identity",
                    principalTable: "Principals",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_TenantMemberships_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "identity",
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MembershipRoles",
            schema: "identity",
            columns: table => new
            {
                TenantMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                RoleId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MembershipRoles", x => new { x.TenantMembershipId, x.RoleId });
                table.ForeignKey(
                    name: "FK_MembershipRoles_Roles_RoleId",
                    column: x => x.RoleId,
                    principalSchema: "identity",
                    principalTable: "Roles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_MembershipRoles_TenantMemberships_TenantMembershipId",
                    column: x => x.TenantMembershipId,
                    principalSchema: "identity",
                    principalTable: "TenantMemberships",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MembershipRoles_RoleId",
            schema: "identity",
            table: "MembershipRoles",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "IX_RolePermissions_PermissionKey",
            schema: "identity",
            table: "RolePermissions",
            column: "PermissionKey");

        migrationBuilder.CreateIndex(
            name: "IX_Roles_TenantId_Key",
            schema: "identity",
            table: "Roles",
            columns: new[] { "TenantId", "Key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ServiceAccounts_ClientId",
            schema: "identity",
            table: "ServiceAccounts",
            column: "ClientId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ServiceAccounts_PrincipalId",
            schema: "identity",
            table: "ServiceAccounts",
            column: "PrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_TenantMemberships_PrincipalId",
            schema: "identity",
            table: "TenantMemberships",
            column: "PrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_TenantMemberships_TenantId_PrincipalId",
            schema: "identity",
            table: "TenantMemberships",
            columns: new[] { "TenantId", "PrincipalId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Tenants_Slug",
            schema: "identity",
            table: "Tenants",
            column: "Slug",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MembershipRoles",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "RolePermissions",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "ServiceAccounts",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "TenantMemberships",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "Permissions",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "Roles",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "Principals",
            schema: "identity");

        migrationBuilder.DropTable(
            name: "Tenants",
            schema: "identity");
    }
}
