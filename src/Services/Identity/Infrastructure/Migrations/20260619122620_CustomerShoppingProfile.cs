using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Identity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class CustomerShoppingProfile : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Addresses_UserId", schema: "identity", table: "Addresses");

        migrationBuilder.AddColumn<string>(name: "FamilyName", schema: "identity", table: "Users", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "GivenName", schema: "identity", table: "Users", type: "text", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "IsDefault", schema: "identity", table: "Addresses", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<string>(
            name: "Purpose",
            schema: "identity",
            table: "Addresses",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "Both");

        migrationBuilder.CreateIndex(
            name: "IX_Addresses_UserId_Purpose_IsDefault",
            schema: "identity",
            table: "Addresses",
            columns: new[] { "UserId", "Purpose", "IsDefault" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Addresses_UserId_Purpose_IsDefault", schema: "identity", table: "Addresses");
        migrationBuilder.DropColumn(name: "FamilyName", schema: "identity", table: "Users");
        migrationBuilder.DropColumn(name: "GivenName", schema: "identity", table: "Users");
        migrationBuilder.DropColumn(name: "IsDefault", schema: "identity", table: "Addresses");
        migrationBuilder.DropColumn(name: "Purpose", schema: "identity", table: "Addresses");
        migrationBuilder.CreateIndex(name: "IX_Addresses_UserId", schema: "identity", table: "Addresses", column: "UserId");
    }
}
