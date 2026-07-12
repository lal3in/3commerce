using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Identity.Infrastructure.Migrations;

/// <inheritdoc />
public partial class StructuredMemberProfile : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateOnly>(
            name: "DateOfBirth",
            schema: "identity",
            table: "Users",
            type: "date",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "FirstName",
            schema: "identity",
            table: "Users",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LastName",
            schema: "identity",
            table: "Users",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "MarketingConsent",
            schema: "identity",
            table: "Users",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "MiddleName",
            schema: "identity",
            table: "Users",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Phone",
            schema: "identity",
            table: "Users",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PreferredName",
            schema: "identity",
            table: "Users",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Title",
            schema: "identity",
            table: "Users",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);

        // Preserve existing names: the old single Given/Family become the structured First/Last.
        migrationBuilder.Sql(
            "UPDATE identity.\"Users\" SET \"FirstName\" = \"GivenName\", \"LastName\" = \"FamilyName\"");

        migrationBuilder.DropColumn(name: "FamilyName", schema: "identity", table: "Users");
        migrationBuilder.DropColumn(name: "GivenName", schema: "identity", table: "Users");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DateOfBirth",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "FirstName",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "LastName",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "MarketingConsent",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "MiddleName",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "Phone",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "PreferredName",
            schema: "identity",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "Title",
            schema: "identity",
            table: "Users");

        migrationBuilder.AddColumn<string>(
            name: "FamilyName",
            schema: "identity",
            table: "Users",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GivenName",
            schema: "identity",
            table: "Users",
            type: "text",
            nullable: true);
    }
}
