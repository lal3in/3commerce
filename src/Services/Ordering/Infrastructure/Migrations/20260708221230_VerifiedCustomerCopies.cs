using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Ordering.Infrastructure.Migrations;

/// <inheritdoc />
public partial class VerifiedCustomerCopies : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VerifiedCustomerCopies",
            schema: "ordering",
            columns: table => new
            {
                Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VerifiedCustomerCopies", x => x.Email);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "VerifiedCustomerCopies",
            schema: "ordering");
    }
}
