using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SubscriptionAutoRenew : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "StorefrontId",
            schema: "payments",
            table: "Subscriptions",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "StorefrontBillingSchedules",
            schema: "payments",
            columns: table => new
            {
                StorefrontId = table.Column<Guid>(type: "uuid", nullable: false),
                DailyRunTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                LastRunOn = table.Column<DateOnly>(type: "date", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StorefrontBillingSchedules", x => x.StorefrontId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Subscriptions_StorefrontId_Status_CurrentPeriodEnd",
            schema: "payments",
            table: "Subscriptions",
            columns: new[] { "StorefrontId", "Status", "CurrentPeriodEnd" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "StorefrontBillingSchedules",
            schema: "payments");

        migrationBuilder.DropIndex(
            name: "IX_Subscriptions_StorefrontId_Status_CurrentPeriodEnd",
            schema: "payments",
            table: "Subscriptions");

        migrationBuilder.DropColumn(
            name: "StorefrontId",
            schema: "payments",
            table: "Subscriptions");
    }
}
