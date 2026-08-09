using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThreeCommerce.Payments.Infrastructure.Migrations;

/// <inheritdoc />
public partial class Mandates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Mandates",
            schema: "payments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                PaymentCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Scheme = table.Column<int>(type: "integer", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                ProviderSetupIntentId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                ProviderMandateId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                ProviderPaymentMethodId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Mandates", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Mandates_ProviderSetupIntentId",
            schema: "payments",
            table: "Mandates",
            column: "ProviderSetupIntentId");

        migrationBuilder.CreateIndex(
            name: "IX_Mandates_UserId",
            schema: "payments",
            table: "Mandates",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Mandates",
            schema: "payments");
    }
}
