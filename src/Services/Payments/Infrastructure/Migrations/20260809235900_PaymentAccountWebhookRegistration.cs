using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class PaymentAccountWebhookRegistration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "WebhookEndpointId",
            schema: "payments",
            table: "PaymentAccounts",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WebhookUrl",
            schema: "payments",
            table: "PaymentAccounts",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "WebhookEndpointId",
            schema: "payments",
            table: "PaymentAccounts");

        migrationBuilder.DropColumn(
            name: "WebhookUrl",
            schema: "payments",
            table: "PaymentAccounts");
    }
}
