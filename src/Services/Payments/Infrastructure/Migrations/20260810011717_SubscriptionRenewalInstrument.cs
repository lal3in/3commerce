using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class SubscriptionRenewalInstrument : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProviderCustomerId",
            schema: "payments",
            table: "Subscriptions",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProviderPaymentMethodId",
            schema: "payments",
            table: "Subscriptions",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProviderCustomerId",
            schema: "payments",
            table: "Subscriptions");

        migrationBuilder.DropColumn(
            name: "ProviderPaymentMethodId",
            schema: "payments",
            table: "Subscriptions");
    }
}
