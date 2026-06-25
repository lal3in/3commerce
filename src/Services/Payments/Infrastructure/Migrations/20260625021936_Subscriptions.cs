using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Subscriptions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Subscriptions",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                CustomerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                VariantId = table.Column<Guid>(type: "uuid", nullable: true),
                BillingPeriod = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                PriceMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                CurrentPeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CurrentPeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Subscriptions", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Subscriptions_OrderId_ProductId_VariantId",
            schema: "payments",
            table: "Subscriptions",
            columns: new[] { "OrderId", "ProductId", "VariantId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Subscriptions_TenantId_CustomerEmail",
            schema: "payments",
            table: "Subscriptions",
            columns: new[] { "TenantId", "CustomerEmail" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Subscriptions",
            schema: "payments");
    }
}
