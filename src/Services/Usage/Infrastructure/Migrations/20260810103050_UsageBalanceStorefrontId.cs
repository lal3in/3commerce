using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Usage.Infrastructure.Migrations;

/// <inheritdoc />
public partial class UsageBalanceStorefrontId : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "StorefrontId",
            schema: "usage",
            table: "UsageBalances",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "StorefrontId",
            schema: "usage",
            table: "UsageBalances");
    }
}
